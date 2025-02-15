﻿namespace Models.Core.ApsimFile
{
    using APSIM.Shared.Utilities;
    using System;
    using System.IO;
    using System.Reflection;
    using System.Linq;
    using Newtonsoft.Json;
    using System.Xml;
    using Newtonsoft.Json.Serialization;
    using System.Collections.Generic;
    using Models.Core.Interfaces;
    using Newtonsoft.Json.Linq;
    using System.Threading.Tasks;

    /// <summary>
    /// A class for reading and writing the .apsimx file format.
    /// </summary>
    /// <remarks>
    /// Features:
    /// * Can WRITE a model in memory to an APSIM Next Generation .json string.
    ///     - Only writes public, settable, properties of a model.
    ///     - If a model implements IDontSerialiseChildren then no child models will be serialised.
    ///     - Won't serialise any property with LinkAttribute.
    /// * Can READ an APSIM Next Generation JSON or XML string to models in memory.
    ///     - Calls converter on the string before deserialisation.
    ///     - Sets fileName property in all simulation models read in.
    ///     - Correctly parents all models.
    ///     - Calls IModel.OnCreated() for all newly created models. If models throw in the
    ///       OnCreated() method, exceptions will be captured and returned to caller along
    ///       with the model tree.
    /// </remarks>
    public class FileFormat
    {
        /// <summary>Convert a model to a string (json).</summary>
        /// <param name="model">The model to serialise.</param>
        /// <returns>The json string.</returns>
        public static string WriteToString(IModel model)
        {
            JsonSerializer serializer = new JsonSerializer()
            {
                DateParseHandling = DateParseHandling.None,
                TypeNameHandling = TypeNameHandling.Objects,
                ContractResolver = new WritablePropertiesOnlyResolver(),
                Formatting = Newtonsoft.Json.Formatting.Indented
            };
            string json;
            using (StringWriter s = new StringWriter())
                using (var writer = new JsonTextWriter(s))
                {
                    serializer.Serialize(writer, model, model.GetType());
                    json = s.ToString();
                }
            return json;
        }

        /// <summary>Create a simulations object by reading the specified filename</summary>
        /// <param name="fileName">Name of the file.</param>
        /// <param name="errorHandler">Action to be taken when an error occurs.</param>
        /// <param name="initInBackground">Iff set to true, the models' OnCreated() method calls will occur in a background thread.</param>
        public static T ReadFromFile<T>(string fileName, Action<Exception> errorHandler, bool initInBackground) where T : IModel
        {
            try
            {
                if (!File.Exists(fileName))
                    throw new Exception("Cannot read file: " + fileName + ". File does not exist.");

                string contents = File.ReadAllText(fileName);
                T newModel = ReadFromString<T>(contents, errorHandler, initInBackground, fileName);

                // Set the filename
                if (newModel is Simulations)
                    (newModel as Simulations).FileName = fileName;
                foreach (Simulation sim in newModel.FindAllDescendants<Simulation>())
                    sim.FileName = fileName;
                return newModel;
            }
            catch (Exception err)
            {
                throw new Exception($"Error reading file {fileName}", err);
            }
        }

        /// <summary>Convert a string (json or xml) to a model.</summary>
        /// <param name="st">The string to convert.</param>
        /// <param name="errorHandler">Action to be taken when an error occurs.</param>
        /// <param name="initInBackground">Iff set to true, the models' OnCreated() method calls will occur in a background thread.</param>
        /// <param name="fileName">The optional filename where the string came from. This is required by the converter, when it needs to modify the .db file.</param>
        public static T ReadFromString<T>(string st, Action<Exception> errorHandler, bool initInBackground, string fileName = null) where T : IModel
        {
            // Run the converter.
            var converter = Converter.DoConvert(st, -1, fileName);

            T newModel;
            JsonSerializerSettings settings = new JsonSerializerSettings()
            {
                TypeNameHandling = TypeNameHandling.Auto,
                DateParseHandling = DateParseHandling.None
            };
            newModel = JsonConvert.DeserializeObject<T>(converter.Root.ToString(), settings);

            if (newModel is Simulations)
                (newModel as Simulations).FileName = fileName;

            // Parent all models.
            newModel.Parent = null;
            try
            {
                newModel.ParentAllDescendants();
            }
            catch (Exception err)
            {
                errorHandler(err);
            }

            // Call created in all models.
            if (initInBackground)
                Task.Run(() => InitialiseModel(newModel, errorHandler));
            else
                InitialiseModel(newModel, errorHandler);

            return newModel;
        }

        private static void InitialiseModel(IModel newModel, Action<Exception> errorHandler)
        {
            foreach (var model in newModel.FindAllDescendants().ToList())
            {
                try
                {
                    model.OnCreated();
                }
                catch (Exception err)
                {
                    errorHandler(err);
                }
            }
        }

        /// <summary>A contract resolver class to only write settable properties.</summary>
        private class WritablePropertiesOnlyResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType)
            {
                var result = base.GetSerializableMembers(objectType);
                result.RemoveAll(m => m is PropertyInfo &&
                                      !(m as PropertyInfo).CanWrite);
                result.RemoveAll(m => m.GetCustomAttribute(typeof(LinkAttribute)) != null);
                return result;
            }

            protected override IValueProvider CreateMemberValueProvider(MemberInfo member)
            {
                if (member.Name == "Children")
                    return new ChildrenProvider(member);

                return base.CreateMemberValueProvider(member);
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);

                if (property.PropertyName == "Children")
                {
                    property.ShouldSerialize = instance =>
                    {
                        if (instance is IOptionallySerialiseChildren opt)
                            return opt.DoSerialiseChildren;

                        return true;
                    };
                }
                else if (typeof(ModelCollectionFromResource).IsAssignableFrom(member.DeclaringType))
                {
                    property.ShouldSerialize = instance =>
                    {
                        var link = member.GetCustomAttribute<LinkAttribute>();
                        var jsonIgnore = member.GetCustomAttribute<JsonIgnoreAttribute>();
                        if (link != null || jsonIgnore != null)
                            return false;

                        // If this property has a description attribute, then it's settable
                        // from the UI, in which case it should always be serialized.
                        var description = member.GetCustomAttribute<DescriptionAttribute>();
                        if (description != null)
                            return true;

                        // If the model is under a replacements node, or if the model doesn't have
                        // a resource name (ie if it's a prototype), then serialize everything.
                        ModelCollectionFromResource resource = instance as ModelCollectionFromResource;
                        if (resource.FindAncestor<Replacements>() != null || string.IsNullOrEmpty(resource.ResourceName))
                            return true;

                        // Otherwise, only serialize if the property is inherited from
                        // Model or ModelCollectionFromResource.
                        return member.DeclaringType.IsAssignableFrom(typeof(ModelCollectionFromResource));
                    };
                }

                return property;
            }

            private class ChildrenProvider : IValueProvider
            {
                private MemberInfo memberInfo;

                public ChildrenProvider(MemberInfo memberInfo)
                {
                    this.memberInfo = memberInfo;
                }

                public object GetValue(object target)
                {
                    if (target is ModelCollectionFromResource m && m.FindAncestor<Replacements>() == null)
                        return m.ChildrenToSerialize;

                    return new ExpressionValueProvider(memberInfo).GetValue(target);
                }

                public void SetValue(object target, object value)
                {
                    new ExpressionValueProvider(memberInfo).SetValue(target, value);
                }
            }
        }
    }
}
