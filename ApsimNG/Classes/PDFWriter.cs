﻿namespace ApsimNG.Classes
{
    using APSIM.Shared.Utilities;
    using Models;
    using Models.Core;
#if NETFRAMEWORK
    using MigraDoc.DocumentObjectModel;
    using MigraDoc.DocumentObjectModel.Shapes;
    using MigraDoc.DocumentObjectModel.Tables;
    using MigraDoc.Rendering;
    using PdfSharp.Drawing;
    using PdfSharp.Fonts;
    using Font = MigraDoc.DocumentObjectModel.Font;
#else
    using MigraDocCore.Rendering;
    using MigraDocCore.DocumentObjectModel;
    using MigraDocCore.DocumentObjectModel.Shapes;
    using MigraDocCore.DocumentObjectModel.Tables;
    using MigraDocCore.DocumentObjectModel.MigraDoc.DocumentObjectModel.Shapes;
    using PdfSharpCore.Drawing;
    using PdfSharpCore.Fonts;
    using Font = MigraDocCore.DocumentObjectModel.Font;
#endif
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using UserInterface.Classes;
    using UserInterface.Commands;
    using UserInterface.Presenters;
    using UserInterface.Views;
    using UserInterface.Interfaces;
    using UserInterface.Extensions;
    using Markdig;

    /// <summary>
    /// This class encapsulates code to convert a list of AutoDocumentation tags to a PDF file.
    /// </summary>
    public class PDFWriter
    {
        private readonly ExplorerPresenter explorerPresenter;
        private readonly bool portrait;
        private BibTeX bibTeX;
        private List<BibTeX.Citation> citations;

#if NETCOREAPP
        /// <summary>
        /// Static constructor to initialise PDFSharp ImageSource.
        /// </summary>
        static PDFWriter()
        {
            if (ImageSource.ImageSourceImpl == null)
                ImageSource.ImageSourceImpl = new PdfSharpCore.Utils.ImageSharpImageSource<SixLabors.ImageSharp.PixelFormats.Rgba32>();
        }
#endif

        /// <summary>Constructor.</summary>
        /// <param name="explorerPresenter">The explorer presenter.</param>
        /// <param name="portraitOrientation">Portrait page orientation?</param>
        public PDFWriter(ExplorerPresenter explorerPresenter, bool portraitOrientation)
        {
            this.explorerPresenter = explorerPresenter;
            this.portrait = portraitOrientation;

            // This is a bit tricky on non-Windows platforms. 
            // Normally PdfSharp tries to get a Windows DC for associated font information
            // See https://alex-maz.info/pdfsharp_150 for the work-around we can apply here.
            // See also http://stackoverflow.com/questions/32726223/pdfsharp-migradoc-font-resolver-for-embedded-fonts-system-argumentexception
            // The work-around is to register our own fontresolver. We don't need to do this on Windows.
            if (!ProcessUtilities.CurrentOS.IsWindows && !(GlobalFontSettings.FontResolver is MyFontResolver))
                GlobalFontSettings.FontResolver = new MyFontResolver();
            
            // Create a temporary working directory.
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "autodoc");
            RelativePath = explorerPresenter.ApsimXFile.FileName;
            if (Directory.Exists(WorkingDirectory))
                Directory.Delete(WorkingDirectory, true);
            Directory.CreateDirectory(WorkingDirectory);
        }

        /// <summary>The directory where the PDFWriter instance is working.</summary>
        public string WorkingDirectory { get; }

        /// <summary>
        /// If images are provided as a relative path name, the full path name will be resolved relative to this path.
        /// </summary>
        public string RelativePath { get; private set; }

        /// <summary>Create the PDF file.</summary>
        /// <param name="tags">The tags to convert to the PDF file.</param>
        /// <param name="fileName">The name of the file to write.</param>
        public void CreatePDF(List<AutoDocumentation.ITag> tags, string fileName)
        {
            string apsimDir = PathUtilities.GetAbsolutePath("%root%", null);
            string bibFile = Path.Combine(apsimDir, "APSIM.bib");
            bibTeX = new BibTeX(bibFile);
            citations = new List<BibTeX.Citation>();
            citations.Clear();

            // Scan for citations.
            ScanForCitations(tags);

            // Create a bibliography.
            CreateBibliography(tags);

            // Strip all blank sections i.e. two headings with nothing between them.
            StripEmptySections(tags);

            // numebr all headings.
            NumberHeadings(tags);

            // Create a MigraDoc document.
            Document document = new Document();
            CreatePDFSyles(document);
            document.DefaultPageSetup.LeftMargin = Unit.FromCentimeter(1);
            document.DefaultPageSetup.TopMargin = Unit.FromCentimeter(1);
            document.DefaultPageSetup.BottomMargin = Unit.FromCentimeter(1);
            document.DefaultPageSetup.Orientation = portrait ? Orientation.Portrait : Orientation.Landscape;

            // Create a MigraDoc section.
            Section section = document.AddSection();

            // Convert all tags to the PDF section.
            TagsToMigraDoc(section, tags);

            // Write the PDF file.
            PdfDocumentRenderer pdfRenderer = new PdfDocumentRenderer(false);
            pdfRenderer.Document = document;
            pdfRenderer.RenderDocument();
            pdfRenderer.PdfDocument.Save(fileName);

            // Remove temporary working directory.
            Directory.Delete(WorkingDirectory, true);
        }

        /// <summary>Creates the PDF syles.</summary>
        /// <param name="document">The document to create the styles in.</param>
        private void CreatePDFSyles(Document document)
        {
            document.Styles["Heading1"].Font.Size = 14;
            document.Styles["Heading1"].Font.Bold = true;
            document.Styles["Heading2"].Font.Size = 12;
            document.Styles["Heading3"].Font.Size = 11;
            document.Styles["Heading4"].Font.Size = 10;
            document.Styles["Heading5"].Font.Size = 9;
            document.Styles["Heading6"].Font.Size = 8;

            Style style = document.Styles.AddStyle("GraphAndTable", "Normal");
            style.Font.Size = 7;
            style.ParagraphFormat.SpaceAfter = Unit.FromCentimeter(0);

            Style xyStyle = document.Styles.AddStyle("GraphAndTable", "Normal");
            xyStyle.Font = new Font("Courier New");

            Style tableStyle = document.Styles.AddStyle("Table", "Normal");
            tableStyle.Font.Size = 10;

            Style smallStyle = document.Styles.AddStyle("Small", "Normal");
            smallStyle.Font.Size = 8;

            Style verySmallStyle = document.Styles.AddStyle("VerySmall", "Normal");
            verySmallStyle.Font.Size = 6;

        }

        /// <summary>Scans for citations.</summary>
        /// <param name="tags">The tags to go through looking for citations.</param>
        private void ScanForCitations(List<AutoDocumentation.ITag> tags)
        {
            foreach (AutoDocumentation.ITag tag in tags)
            {
                if (tag is AutoDocumentation.Paragraph)
                {
                    AutoDocumentation.Paragraph paragraph = tag as AutoDocumentation.Paragraph;
                    string text = paragraph.text;

                    // citations are of the form [Brown et al. 2014][brown_plant_2014]
                    // where the second bracketed value is the bibliography reference name. i.e.
                    // the bit we're interested in.
                    int posBracket = text.IndexOf('[');
                    while (posBracket != -1)
                    {
                        int posEndBracket = text.IndexOf(']', posBracket);
                        if (posEndBracket != -1)
                        {
                            // found a possible citation.
                            string citationName = text.Substring(posBracket + 1, posEndBracket - posBracket - 1);
                            string[] inTextCitations = citationName.Split("; ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                            string replacementText = string.Empty;

                            foreach (string inTextCitation in inTextCitations)
                            {
                                // see if we have already encountered the citation.
                                BibTeX.Citation citation = citations.Find(c => c.Name == inTextCitation);

                                // If we haven't encountered it, look it up in the .bib file.
                                if (citation == null)
                                {
                                    citation = bibTeX.Lookup(inTextCitation);
                                    if (citation != null)
                                        citations.Add(citation);
                                }

                                if (citation != null)
                                {
                                    // Replace the in-text citation with (author et al., year)
                                    if (replacementText != string.Empty)
                                        replacementText += "; ";
                                    replacementText += string.Format("<a href=\"#{0}\">{1}</a>", citation.Name, citation.InTextCite);
                                }
                            }

                            if (replacementText != string.Empty)
                            {
                                text = text.Remove(posBracket, posEndBracket - posBracket + 1);
                                text = text.Insert(posBracket, replacementText);
                            }
                        }

                        // Find the next bracketed potential citation.
                        posBracket = text.IndexOf('[', posEndBracket + 1);
                    }

                    paragraph.text = text;
                }
            }
        }

        /// <summary>Creates the bibliography.</summary>
        /// <param name="tags">The tags to add to.</param>
        private void CreateBibliography(List<AutoDocumentation.ITag> tags)
        {
            if (citations.Count > 0)
            {
                // Create the heading.
                tags.Add(new AutoDocumentation.Heading("References", 1));

                citations.Sort(new BibTeX.CitationComparer());
                foreach (BibTeX.Citation citation in citations)
                {
                    string url = citation.URL;
                    string text;
                    if (url != string.Empty)
                        text = string.Format("<a href=\"{0}\">{1}</a>", url, citation.BibliographyText);
                    else
                        text = citation.BibliographyText;

                    AutoDocumentation.Paragraph paragraph = new AutoDocumentation.Paragraph(text, 0);
                    paragraph.bookmarkName = citation.Name;
                    paragraph.handingIndent = true;
                    tags.Add(paragraph);
                }
            }
        }

        /// <summary>Strip all blank sections i.e. two headings with nothing between them.</summary>
        /// <param name="tags"></param>
        private void StripEmptySections(List<AutoDocumentation.ITag> tags)
        {
            bool tagsRemoved;
            do
            {
                tagsRemoved = false;
                for (int i = 0; i < tags.Count - 1; i++)
                {
                    AutoDocumentation.Heading thisTag = tags[i] as AutoDocumentation.Heading;
                    AutoDocumentation.Heading nextTag = tags[i + 1] as AutoDocumentation.Heading;
                    if (thisTag != null && nextTag != null && (thisTag.headingLevel >= nextTag.headingLevel && nextTag.headingLevel != -1))
                    {
                        // Need to renumber headings after this tag until we get to the same heading
                        // level that thisTag is on.
                        tags.RemoveAt(i);
                        i--;
                        tagsRemoved = true;
                    }
                }
            }
            while (tagsRemoved);

            // Adjust heading levels so we don't have heading levels that have gaps e.g. go from 1 to 3
            int previousHeadingLevel = 0;
            for (int i = 0; i < tags.Count - 1; i++)
            {
                var thisTag = tags[i] as AutoDocumentation.Heading;
                if (thisTag != null)
                { 
                    var gapInHeadingLevel = thisTag.headingLevel - previousHeadingLevel;
                    if (gapInHeadingLevel > 1)
                        RemoveGapInHeadingLevel(tags, i, gapInHeadingLevel - 1);
                    previousHeadingLevel = thisTag.headingLevel;
                }
            }
        }

        /// <summary>
        /// Remove the gap in heading levels for a 'branch' of tags.
        /// </summary>
        /// <param name="tags">List of tags.</param>
        /// <param name="tagIndex">Index of the tag to be removed.</param>
        /// <param name="deltaHeadingLevel">Change heading level by this amount.</param>
        private void RemoveGapInHeadingLevel(List<AutoDocumentation.ITag> tags, int tagIndex, int deltaHeadingLevel)
        {
            int referenceHeadingLevel = (tags[tagIndex] as AutoDocumentation.Heading).headingLevel;
            while (tagIndex + 1 < tags.Count)
            {
                var tag = tags[tagIndex] as AutoDocumentation.Heading;
                if (tag != null)
                {
                    if (tag.headingLevel >= referenceHeadingLevel)
                        tag.headingLevel -= deltaHeadingLevel;   // apply delta heading level
                    else
                        break;   // branch has ended so exit.
                }
                tagIndex++;
            }
        }

        /// <summary>Creates a table of contents.</summary>
        /// <param name="tags">The autodoc tags.</param>
        private void NumberHeadings(List<AutoDocumentation.ITag> tags)
        {
            int[] levels = new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            foreach (AutoDocumentation.ITag tag in tags)
            {
                if (tag is AutoDocumentation.Heading)
                {
                    AutoDocumentation.Heading heading = tag as AutoDocumentation.Heading;
                    if (heading.headingLevel > 0 && heading.text != "TitlePage")
                    {
                        // Update levels based on heading number.
                        levels[heading.headingLevel - 1]++;
                        for (int i = heading.headingLevel; i < levels.Length; i++)
                            if (levels[i] > 0)
                                levels[i] = 0;

                        // Insert the levels into the heading e.g. "1.3.2"
                        string levelString = string.Empty;
                        for (int i = 0; i < levels.Length; i++)
                            if (levels[i] > 0)
                            {
                                if (levelString != string.Empty)
                                    levelString += ".";
                                levelString += levels[i].ToString();
                            }
                        heading.text = levelString + " " + heading.text;
                    }
                }
            }
        }
        
        /// <summary>Writes PDF for specified auto-doc commands.</summary>
        /// <param name="section">The writer to write to.</param>
        /// <param name="tags">The autodoc tags.</param>
        private void TagsToMigraDoc(Section section, List<AutoDocumentation.ITag> tags)
        {
            int figureNumber = 0;
            foreach (AutoDocumentation.ITag tag in tags)
            {
                if (tag is AutoDocumentation.Heading)
                {
                    AutoDocumentation.Heading heading = tag as AutoDocumentation.Heading;
                    if (heading.headingLevel > 0 && heading.headingLevel <= 6)
                    {
                        Paragraph para = section.AddParagraph(heading.text, "Heading" + heading.headingLevel);
                        para.Format.KeepWithNext = true;
                        int posSpace = heading.text.IndexOf(' ');
                        if (posSpace > 0)
                            para.AddBookmark(heading.text.Substring(posSpace + 1));
                        if (heading.headingLevel == 1)
                            para.Format.OutlineLevel = OutlineLevel.Level1;
                        else if (heading.headingLevel == 2)
                            para.Format.OutlineLevel = OutlineLevel.Level2;
                        else if (heading.headingLevel == 3)
                            para.Format.OutlineLevel = OutlineLevel.Level3;
                        else if (heading.headingLevel == 4)
                            para.Format.OutlineLevel = OutlineLevel.Level4;
                        else if (heading.headingLevel == 5)
                            para.Format.OutlineLevel = OutlineLevel.Level5;
                        else if (heading.headingLevel == 6)
                            para.Format.OutlineLevel = OutlineLevel.Level6;
                    }
                }
                else if (tag is AutoDocumentation.Paragraph)
                {
                    AutoDocumentation.Paragraph paragraph = tag as AutoDocumentation.Paragraph;
                    if (paragraph.text.Contains("![Alt Text]"))
                        figureNumber++;
                    paragraph.text = paragraph.text.Replace("[FigureNumber]", figureNumber.ToString());
                    AddFormattedParagraphToSection(section, paragraph);
                }
                else if (tag is AutoDocumentation.GraphAndTable)
                    CreateGraphPDF(section, tag as AutoDocumentation.GraphAndTable);
                else if (tag is GraphPage)
                    CreateGraphPage(section, tag as GraphPage);
                else if (tag is AutoDocumentation.NewPage)
                {
                    section.AddPageBreak();
                    if ((tag as AutoDocumentation.NewPage).Portrait)
                        section.PageSetup.Orientation = Orientation.Portrait;
                    else
                        section.PageSetup.Orientation = Orientation.Landscape;
                }
                else if (tag is AutoDocumentation.PageSetup)
                {
                    if ((tag as AutoDocumentation.PageSetup).Portrait)
                        section.PageSetup.Orientation = Orientation.Portrait;
                    else
                        section.PageSetup.Orientation = Orientation.Landscape;
                }
                else if (tag is AutoDocumentation.Table)
                    CreateTable(section, tag as AutoDocumentation.Table);
                else if (tag is Graph)
                {
                    GraphPresenter graphPresenter = new GraphPresenter();
                    explorerPresenter.ApsimXFile.Links.Resolve(graphPresenter);
                    GraphView graphView = new GraphView();
                    graphView.BackColor = OxyPlot.OxyColors.White;
                    graphView.ForegroundColour = OxyPlot.OxyColors.Black;
                    graphView.FontSize = 12;
                    graphView.Width = 500;
                    graphView.Height = 500;
                    graphPresenter.Attach(tag, graphView, explorerPresenter);
                    string pngFileName = graphPresenter.ExportToPNG(WorkingDirectory);
                    section.AddResizeImage(pngFileName);
                    string caption = (tag as Graph).Caption;
                    if (caption != null)
                        section.AddParagraph(caption);
                    graphPresenter.Detach();
                    graphView.MainWidget.Cleanup();
                }
                else if (tag is Map && (tag as Map).GetCoordinates().Count > 0)
                {
#if NETFRAMEWORK
                    MapPresenter mapPresenter = new MapPresenter();
                    MapView mapView = new MapView(null);
                    mapPresenter.Attach(tag, mapView, explorerPresenter);
                    var map = mapView.Export();
                    string pngFileName = Path.ChangeExtension(Path.GetTempFileName(), ".png");
                    if (map.Width > section.PageSetup.PageWidth)
                        map = ImageUtilities.ResizeImage(map, section.PageSetup.PageWidth, double.MaxValue);
                    map.Save(pngFileName);
                    if (!String.IsNullOrEmpty(pngFileName))
                        section.AddResizeImage(pngFileName);
                    mapPresenter.Detach();
                    mapView.MainWidget.Destroy();
#else
                    section.AddParagraph("MapView has not been implemented in gtk3. Use the framework/gtk2 build instead.");
#endif
                }
                else if (tag is AutoDocumentation.Image)
                {
                    AutoDocumentation.Image imageTag = tag as AutoDocumentation.Image;
                    string pngFileName = Path.Combine(WorkingDirectory, $"{imageTag.name}.png");
                    imageTag.image.Save(pngFileName, System.Drawing.Imaging.ImageFormat.Png);
                    section.AddResizeImage(pngFileName);
                    figureNumber++;
                }
                else if (tag is AutoDocumentation.ModelView)
                {
                    try
                    {
                        AutoDocumentation.ModelView modelView = tag as AutoDocumentation.ModelView;
                        ViewNameAttribute viewName = ReflectionUtilities.GetAttribute(modelView.model.GetType(), typeof(ViewNameAttribute), false) as ViewNameAttribute;
                        PresenterNameAttribute presenterName = ReflectionUtilities.GetAttribute(modelView.model.GetType(), typeof(PresenterNameAttribute), false) as PresenterNameAttribute;
                        if (viewName != null && presenterName != null)
                        {
                            ViewBase owner = ViewBase.MasterView as ViewBase;
                            if (viewName.ToString() == "UserInterface.Views.MapView")
                                owner = null;

                            ViewBase view = Assembly.GetExecutingAssembly().CreateInstance(viewName.ToString(), false, BindingFlags.Default, null, new object[] { owner }, null, null) as ViewBase;
                            IPresenter presenter = Assembly.GetExecutingAssembly().CreateInstance(presenterName.ToString()) as IPresenter;

                            if (view != null && presenter != null)
                            {
                                explorerPresenter.ApsimXFile.Links.Resolve(presenter);
                                presenter.Attach(modelView.model, view, explorerPresenter);

#if NETFRAMEWORK
                                Gtk.Window popupWin = new Gtk.Window(Gtk.WindowType.Popup);
#endif
                                try
                                {
#if NETFRAMEWORK
                                    popupWin.SetSizeRequest(700, 700);
                                    popupWin.Add(view.MainWidget);
#endif

                                    if (view is MapView map)
                                        map.HideZoomControls();
#if NETFRAMEWORK
                                    popupWin.ShowAll();
#endif
                                    while (Gtk.Application.EventsPending())
                                        Gtk.Application.RunIteration();

                                    // From MapView:
                                    // With WebKit, it appears we need to give it time to actually update the display
                                    // Really only a problem with the temporary windows used for generating documentation
                                    string pngFileName;
                                    if (view is MapView mapView)
                                    {
                                        var img = mapView.Export();
                                        pngFileName = Path.ChangeExtension(Path.GetTempFileName(), ".png");
                                        if (section.PageSetup.PageWidth > 0 && img.Width > section.PageSetup.PageWidth)
                                            img = ImageUtilities.ResizeImage(img, section.PageSetup.PageWidth, double.MaxValue);
                                        img.Save(pngFileName);
                                    }
                                    else
                                        pngFileName = (presenter as IExportable).ExportToPNG(WorkingDirectory);
                                    section.AddResizeImage(pngFileName);
                                    presenter.Detach();
                                    view.MainWidget.Cleanup();
                                }
                                finally
                                {
#if NETFRAMEWORK
                                    popupWin.Cleanup();
#endif
                                    while (GLib.MainContext.Iteration());
                                }
                            }
                        }
                    }
                    catch (Exception err)
                    {
                        Console.WriteLine(err);
                    }
                }
            }
        }

        /// <summary>Adds a formatted paragraph to section.</summary>
        /// <param name="section">The section.</param>
        /// <param name="paragraph">The paragraph.</param>
        private void AddFormattedParagraphToSection(Section section, AutoDocumentation.Paragraph paragraph)
        {
            string html = Markdown.ToHtml(paragraph.text, new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build());

            HtmlToMigraDoc.Convert(html, section, WorkingDirectory, RelativePath);

            Paragraph para = section.LastParagraph;
            para.Format.LeftIndent += Unit.FromCentimeter(paragraph.indent);
            if (paragraph.bookmarkName != null)
                para.AddBookmark(paragraph.bookmarkName);
            if (paragraph.handingIndent)
            {
                para.Format.LeftIndent = "1cm";
                para.Format.FirstLineIndent = "-1cm";
            }
        }

        /// <summary>Creates the graph.</summary>
        /// <param name="section">The section.</param>
        /// <param name="graphAndTable">The graph and table to convert to html.</param>
        private void CreateGraphPDF(Section section, AutoDocumentation.GraphAndTable graphAndTable)
        {
            // Create a 2 column, 1 row table. Image in first cell, X/Y data in second cell.
            var table = section.AddTable();
            table.Style = "GraphAndTable";
            table.Rows.LeftIndent = graphAndTable.indent + "cm";

            var column1 = table.AddColumn();
            column1.Width = "8cm";
            //column1.Format.Alignment = ParagraphAlignment.Right;
            var column2 = table.AddColumn();
            column2.Width = "8cm";
            //column2.Format.Alignment = ParagraphAlignment.Right;
            var row = table.AddRow();

            // Ensure graphs directory exists.
            string graphDirectory = Path.Combine(WorkingDirectory, "Graphs");
            Directory.CreateDirectory(graphDirectory);

            // Determine the name of the .png file to write.
            string pngFileName = Path.Combine(graphDirectory,
                                              graphAndTable.xyPairs.Parent.Parent.Name + graphAndTable.xyPairs.Parent.Name + ".png");
            int count = 0;
            // If there are multiple graphs with the same name, they may overwrite each other.
            // Therefore, we attempt to generate a unique name. After 20 attempts, we give up.
            while (File.Exists(pngFileName) && count < 20)
            {
                count++;
                pngFileName = Path.Combine(graphDirectory, graphAndTable.xyPairs.Parent.Parent.Name + graphAndTable.xyPairs.Parent.Name + Guid.NewGuid() + ".png");
            }
            // Setup graph.
            GraphView graph = new GraphView();
            graph.Clear();
            int width = 400;
            int height = 250;
            graph.Width = width;
            graph.Height = height;

            // Create a line series.
            graph.DrawLineAndMarkers("", graphAndTable.xyPairs.X, graphAndTable.xyPairs.Y, null, null, null, null,
                                     Models.Axis.AxisType.Bottom, Models.Axis.AxisType.Left,
                                     System.Drawing.Color.Blue, Models.LineType.Solid, Models.MarkerType.None,
                                     Models.LineThicknessType.Normal, Models.MarkerSizeType.Normal, 1, true);

            graph.ForegroundColour = OxyPlot.OxyColors.Black;
            graph.BackColor = OxyPlot.OxyColors.White;
            // Format the axes.
            graph.FormatAxis(Models.Axis.AxisType.Bottom, graphAndTable.xName, false, double.NaN, double.NaN, double.NaN, false);

            var units = AutoDocumentation.GetUnits(graphAndTable.xyPairs, graphAndTable.yName);
            var yaxisTitle = graphAndTable.yName;
            if (!string.IsNullOrEmpty(units))
                yaxisTitle += " (" + units + ")";
            graph.FormatAxis(Models.Axis.AxisType.Left, yaxisTitle, false, double.NaN, double.NaN, double.NaN, false);
            graph.FontSize = 10;
            graph.Refresh();

            // Export graph to bitmap file.
            Bitmap image = new Bitmap(width, height);
            using (Graphics gfx = Graphics.FromImage(image))
            using (SolidBrush brush = new SolidBrush(System.Drawing.Color.White))
            {
                gfx.FillRectangle(brush, 0, 0, image.Width, image.Height);
            }
            graph.Export(ref image, new Rectangle(0, 0, image.Width, image.Height), false);
            graph.MainWidget.Cleanup();
            image.Save(pngFileName, System.Drawing.Imaging.ImageFormat.Png);
#if NETFRAMEWORK
            var sectionImage = row.Cells[0].AddImage(pngFileName);
#else
            var sectionImage = row.Cells[0].AddImage(ImageSource.FromFile(pngFileName));
#endif
            sectionImage.LockAspectRatio = true;
            sectionImage.Width = "8cm";

            // Add x/y data.
            Paragraph xyParagraph = row.Cells[1].AddParagraph();
            xyParagraph.Style = "xyStyle";
            AddFixedWidthText(xyParagraph, "X", 10);
            AddFixedWidthText(xyParagraph, "Y", 10);
            xyParagraph.AddLineBreak();
            for (int i = 0; i < graphAndTable.xyPairs.X.Length; i++)
            {

                AddFixedWidthText(xyParagraph, graphAndTable.xyPairs.X[i].ToString(), 10);
                AddFixedWidthText(xyParagraph, graphAndTable.xyPairs.Y[i].ToString(), 10);
                xyParagraph.AddLineBreak();
            }

            // Add an empty paragraph for spacing.
            section.AddParagraph();
        }

        /// <summary>Creates the graph page.</summary>
        /// <param name="section">The section to write to.</param>
        /// <param name="graphPage">The graph and table to convert to html.</param>
        private void CreateGraphPage(Section section, GraphPage graphPage)
        {
            int numGraphsToPrint = graphPage.Graphs.FindAll(g => g.IncludeInDocumentation && g.Enabled).Count;
            if (numGraphsToPrint > 0)
            {
                int numColumns = 2;
                int numRows = (numGraphsToPrint + 1) / numColumns;

                // Export graph to bitmap file.
                Bitmap image = new Bitmap(1800, numRows * 600);
                using (Graphics gfx = Graphics.FromImage(image))
                using (SolidBrush brush = new SolidBrush(System.Drawing.Color.White))
                {
                    gfx.FillRectangle(brush, 0, 0, image.Width, image.Height);
                }
                GraphPresenter graphPresenter = new GraphPresenter();
                explorerPresenter.ApsimXFile.Links.Resolve(graphPresenter);
                GraphView graphView = new GraphView();
                graphView.BackColor = OxyPlot.OxyColors.White;
                graphView.ForegroundColour = OxyPlot.OxyColors.Black;
                graphView.FontSize = 22;
                graphView.MarkerSize = MarkerSizeType.Normal;
                int width = image.Width / numColumns;
                int height = image.Height / numRows;
                graphView.Width = width;
                graphView.Height = height;
                graphView.LeftRightPadding = 0;

                int col = 0;
                int row = 0;
                for (int i = 0; i < graphPage.Graphs.Count; i++)
                {
                    if (graphPage.Graphs[i].IncludeInDocumentation && graphPage.Graphs[i].Enabled)
                    {
                        try
                        {
                            graphPresenter.Attach(graphPage.Graphs[i], graphView, explorerPresenter);
                            Rectangle r = new Rectangle(col * width, row * height,
                                                        width, height);
                            graphView.Export(ref image, r, false);
                            graphPresenter.Detach();
                            col++;
                            if (col >= numColumns)
                            {
                                col = 0;
                                row++;
                            }
                        }
                        catch (Exception err)
                        {
                            throw new Exception($"Unable to draw graph '{graphPage.Graphs[i].FullPath}'", err);
                        }
                    }
                }

                string basePngFileName = graphPage.Graphs[0].Parent.FullPath + "." +
                                                        graphPage.Name + ".png";
                basePngFileName = basePngFileName.TrimStart('.');
                string pngFileName = Path.Combine(WorkingDirectory, basePngFileName);
                image.Save(pngFileName, System.Drawing.Imaging.ImageFormat.Png);

#if NETFRAMEWORK
                var sectionImage = section.AddImage(pngFileName);
#else
                var sectionImage = section.AddImage(ImageSource.FromFile(pngFileName));
#endif
                sectionImage.LockAspectRatio = true;
                sectionImage.Width = "19cm";
            }
        }

        /// <summary>Creates the table.</summary>
        /// <param name="section">The section.</param>
        /// <param name="tableObj">The table to convert to html.</param>
        private void CreateTable(Section section, AutoDocumentation.Table tableObj)
        {
            var table = section.AddTable();
            table.Style = tableObj.Style;
            table.Borders.Color = Colors.Blue;
            table.Borders.Width = 0.25;
            table.Borders.Left.Width = 0.5;
            table.Borders.Right.Width = 0.5;
            table.Rows.LeftIndent = 0;

            var fontSize = section.Document.Styles[tableObj.Style].Font.Size.Value;
            var gdiFont = new XFont("Arial", fontSize);
            XGraphics graphics = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);

            // Add the required columns to the table.
            foreach (DataColumn column in tableObj.data.Table.Columns)
            {
                var column1 = table.AddColumn();
                column1.Format.Alignment = ParagraphAlignment.Right;
            }

            // Add a heading row.
            var headingRow = table.AddRow();
            headingRow.HeadingFormat = true;
            headingRow.Format.Font.Bold = true;
            headingRow.Shading.Color = Colors.LightBlue;

            for (int columnIndex = 0; columnIndex < tableObj.data.Table.Columns.Count; columnIndex++)
            {
                // Get column heading
                string heading = tableObj.data.Table.Columns[columnIndex].ColumnName;
                headingRow.Cells[columnIndex].AddParagraph(heading);

                // Get the width of the column
                double maxSize = graphics.MeasureString(heading, gdiFont).Width;
                for (int rowIndex = 0; rowIndex < tableObj.data.Count; rowIndex++)
                {
                    // Add a row to our table if processing first column.
                    Row row;
                    if (columnIndex == 0)
                        table.AddRow();

                    // Get the row to process.
                    row = table.Rows[rowIndex+1];

                    // Convert potential HTML to the cell in our row.
                    HtmlToMigraDoc.Convert(tableObj.data[rowIndex][columnIndex].ToString(),
                                           row.Cells[columnIndex], 
                                           WorkingDirectory,
                                           RelativePath);

                    // Update the maximum size of the column with the value from the current row.
                    foreach (var element in row.Cells[columnIndex].Elements)
                    {
                        if (element is Paragraph)
                        {
                            var paragraph = element as Paragraph;
                            var contents = string.Empty;
                            foreach (var paragraphElement in paragraph.Elements)
                                if (paragraphElement is Text)
                                    contents += (paragraphElement as Text).Content;
                                else if (paragraphElement is Hyperlink)
                                    contents += (paragraphElement as Hyperlink).Name;

                            var size = graphics.MeasureString(contents, gdiFont);
                            maxSize = Math.Max(maxSize, size.Width);
                        }
                    }
                }

                // Increase maxSize by a 'padding' value to allow for padding between the table cell
                // contents and the table borders.
                maxSize += 20;

                // maxWidth is the maximum allowed width of the column. E.g. if tableObj.ColumnWidth
                // is 50, then maxWidth is the amount of space taken up by 50 characters.
                // maxSize, on the other hand, is the length of the longest string in the column.
                // The actual column width is whichever of these two values is smaller.
                // MigraDoc will automatically wrap text to ensure the column respects this width.
                double maxWidth = graphics.MeasureString(new string('m', tableObj.ColumnWidth), gdiFont).Width;
                table.Columns[columnIndex].Width = Unit.FromPoint(Math.Min(maxWidth, maxSize) + 0);
            }
            
            section.AddParagraph();
        }

        /// <summary>
        /// Adds fixed-width text to a MigraDoc paragraph
        /// </summary>
        /// <param name="paragraph">The paragaraph to add text to</param>
        /// <param name="text">The text</param>
        /// <param name="width">Width of the text.</param>
        private static void AddFixedWidthText(Paragraph paragraph, string text, int width)
        {
            // For some reason, a parapraph converts all sequences of white
            // space to a single space.  Thus we need to split the text and add
            // the spaces using the AddSpace function.

            int numSpaces = width - text.Length;

            paragraph.AddSpace(numSpaces);
            paragraph.AddText(text);
        }
    }

    public class MyFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // Ignore case of font names.
            var name = familyName.ToLower();

            // Deal with the fonts we know.
            if (name.StartsWith("courier"))
                return new FontResolverInfo("Courier#");
            else
            {
                if (isBold)
                {
                    if (isItalic)
                        return new FontResolverInfo("Arial#bi");
                    return new FontResolverInfo("Arial#b");
                }
                if (isItalic)
                    return new FontResolverInfo("Arial#i");
                return new FontResolverInfo("Arial#");
            }
        }

        /// <summary>
        /// Return the font data for the fonts.
        /// </summary>
        public byte[] GetFont(string faceName)
        {
            switch (faceName)
            {
                case "Courier#":
                    return MyFontHelper.Courier;

                case "Arial#":
                    return MyFontHelper.Arial;

                case "Arial#b":
                    return MyFontHelper.ArialBold;

                case "Arial#i":
                    return MyFontHelper.ArialItalic;

                case "Arial#bi":
                    return MyFontHelper.ArialBoldItalic;
            }
            return null;
        }
    }

    /// <summary>
    /// Helper class that reads font data from embedded resources.
    /// </summary>
    public static class MyFontHelper
    {
        public static byte[] Courier
        {
            get { return LoadFontData("ApsimNG.Resources.Fonts.cour.ttf"); }
        }

        // Make sure the fonts have compile type "Embedded Resource". Names are case-sensitive.
        public static byte[] Arial
        {
            get { return LoadFontData("ApsimNG.Resources.Fonts.arial.ttf"); }
        }

        public static byte[] ArialBold
        {
            get { return LoadFontData("ApsimNG.Resources.Fonts.arialbd.ttf"); }
        }

        public static byte[] ArialItalic
        {
            get { return LoadFontData("ApsimNG.Resources.Fonts.ariali.ttf"); }
        }

        public static byte[] ArialBoldItalic
        {
            get { return LoadFontData("ApsimNG.Resources.Fonts.arialbi.ttf"); }
        }

        /// <summary>
        /// Returns the specified font from an embedded resource.
        /// </summary>
        static byte[] LoadFontData(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new ArgumentException("No resource with name " + name);

                int count = (int)stream.Length;
                byte[] data = new byte[count];
                stream.Read(data, 0, count);
                return data;
            }
        }
    }


}