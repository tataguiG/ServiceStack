using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ServiceStack.Host;

namespace ServiceStack.NativeTypes.VbNet
{
    public class VbNetGenerator
    {
        private const int Version = 1;

        readonly MetadataTypesConfig Config;

        public VbNetGenerator(MetadataTypesConfig config)
        {
            Config = config;
        }

        class CreateTypeOptions
        {
            public Func<string> ImplementsFn { get; set; }
            public bool IsRequest { get; set; }
            public bool IsResponse { get; set; }
            public bool IsOperation { get { return IsRequest || IsResponse; } }
            public bool IsType { get; set; }
            public bool IsNestedType { get; set; }
        }

        public string GetCode(MetadataTypes metadata)
        {
            var namespaces = new HashSet<string>();
            Config.DefaultNamespaces.Each(x => namespaces.Add(x));
            metadata.Types.Each(x => namespaces.Add(x.Namespace));
            metadata.Operations.Each(x => namespaces.Add(x.Request.Namespace));

            var sb = new StringBuilderWrapper(new StringBuilder());
            sb.AppendLine("' Options:");
            sb.AppendLine("'Version: {0}".Fmt(Version));
            sb.AppendLine("'BaseUrl: {0}".Fmt(Config.BaseUrl));
            sb.AppendLine();
            sb.AppendLine("'ServerVersion: {0}".Fmt(metadata.Version));
            sb.AppendLine("'MakePartial: {0}".Fmt(Config.MakePartial));
            sb.AppendLine("'MakeOverridable: {0}".Fmt(Config.MakeVirtual));
            sb.AppendLine("'MakeDataContractsExtensible: {0}".Fmt(Config.MakeDataContractsExtensible));
            sb.AppendLine("'AddReturnMarker: {0}".Fmt(Config.AddReturnMarker));
            sb.AppendLine("'AddDescriptionAsComments: {0}".Fmt(Config.AddDescriptionAsComments));
            sb.AppendLine("'AddDataContractAttributes: {0}".Fmt(Config.AddDataContractAttributes));
            sb.AppendLine("'AddIndexesToDataMembers: {0}".Fmt(Config.AddIndexesToDataMembers));
            sb.AppendLine("'AddResponseStatus: {0}".Fmt(Config.AddResponseStatus));
            sb.AppendLine("'AddImplicitVersion: {0}".Fmt(Config.AddImplicitVersion));
            sb.AppendLine("'InitializeCollections: {0}".Fmt(Config.InitializeCollections));
            sb.AppendLine("'AddDefaultXmlNamespace: {0}".Fmt(Config.AddDefaultXmlNamespace));
            //sb.AppendLine("'DefaultNamespaces: {0}".Fmt(Config.DefaultNamespaces.ToArray().Join(", ")));
            sb.AppendLine();

            namespaces.Each(x => sb.AppendLine("Imports {0}".Fmt(x)));

            if (Config.AddDataContractAttributes
                && Config.AddDefaultXmlNamespace != null)
            {
                sb.AppendLine();

                namespaces.Where(x => !Config.DefaultNamespaces.Contains(x)).ToList()
                    .ForEach(x =>
                        sb.AppendLine("<Assembly: ContractNamespace(\"{0}\", ClrNamespace:=\"{1}\")>"
                            .Fmt(Config.AddDefaultXmlNamespace, x)));
            }

            sb.AppendLine();

            string lastNS = null;

            var existingOps = new HashSet<string>();

            var requestTypes = metadata.Operations.Select(x => x.Request).ToHashSet();
            var requestTypesMap = metadata.Operations.ToSafeDictionary(x => x.Request);
            var responseTypes = metadata.Operations
                .Where(x => x.Response != null)
                .Select(x => x.Response).ToHashSet();
            var types = metadata.Types.ToHashSet();

            var allTypes = new List<MetadataType>();
            allTypes.AddRange(requestTypes);
            allTypes.AddRange(responseTypes);
            allTypes.AddRange(types);
            var orderedTypes = allTypes
                .OrderBy(x => x.Namespace)
                .ThenBy(x => x.Name);

            foreach (var type in orderedTypes)
            {
                var fullTypeName = type.GetFullName();
                if (requestTypes.Contains(type))
                {
                    if (!existingOps.Contains(fullTypeName))
                    {
                        MetadataType response = null;
                        MetadataOperationType operation;
                        if (requestTypesMap.TryGetValue(type, out operation))
                        {
                            response = operation.Response;
                        }

                        lastNS = AppendType(ref sb, type, lastNS, allTypes,
                            new CreateTypeOptions
                            {
                                ImplementsFn = () =>
                                {
                                    if (!Config.AddReturnMarker
                                        && !type.ReturnVoidMarker
                                        && type.ReturnMarkerTypeName == null)
                                        return null;

                                    if (type.ReturnVoidMarker)
                                        return "IReturnVoid";
                                    if (type.ReturnMarkerTypeName != null)
                                        return Type("IReturn`1", new[] { Type(type.ReturnMarkerTypeName) });
                                    return response != null
                                                ? Type("IReturn`1", new[] { Type(type.Name, type.GenericArgs) })
                                                : null;
                                },
                                IsRequest = true,
                            });

                        existingOps.Add(fullTypeName);
                    }
                }
                else if (responseTypes.Contains(type))
                {
                    if (!existingOps.Contains(fullTypeName)
                        && !Config.IgnoreTypesInNamespaces.Contains(type.Namespace))
                    {
                        lastNS = AppendType(ref sb, type, lastNS, allTypes,
                            new CreateTypeOptions { IsResponse = true, });

                        existingOps.Add(fullTypeName);
                    }
                }
                else if (types.Contains(type) && !existingOps.Contains(fullTypeName))
                {
                    lastNS = AppendType(ref sb, type, lastNS, allTypes,
                        new CreateTypeOptions { IsType = true });
                }
            }

            if (lastNS != null)
                sb.AppendLine("End Namespace");
            sb.AppendLine();

            return sb.ToString();
        }

        private string AppendType(ref StringBuilderWrapper sb, MetadataType type, string lastNS, List<MetadataType> allTypes, CreateTypeOptions options)
        {
            if (type == null ||
                (type.IsNested.GetValueOrDefault() && !options.IsNestedType) ||
                (type.Namespace != null && type.Namespace.StartsWith("System")))
                return lastNS;

            if (type.Namespace != lastNS)
            {
                if (lastNS != null)
                    sb.AppendLine("End Namespace");

                lastNS = type.Namespace;

                sb.AppendLine();
                sb.AppendLine("Namespace {0}".Fmt(type.Namespace.SafeToken()));
                //sb.AppendLine("{");
            }

            sb = sb.Indent();

            sb.AppendLine();
            AppendComments(sb, type.Description);
            if (type.Routes != null)
            {
                AppendAttributes(sb, type.Routes.ConvertAll(x => x.ToMetadataAttribute()));
            }
            AppendAttributes(sb, type.Attributes);
            AppendDataContract(sb, type.DataContract);

            if (type.IsEnum.GetValueOrDefault())
            {
                sb.AppendLine("Public Enum {0}".Fmt(Type(type.Name, type.GenericArgs)));
                //sb.AppendLine("{");
                sb = sb.Indent();

                if (type.EnumNames != null)
                {
                    for (var i = 0; i < type.EnumNames.Count; i++)
                    {
                        var name = type.EnumNames[i];
                        var value = type.EnumValues != null ? type.EnumValues[i] : null;
                        sb.AppendLine(value == null
                            ? "{0},".Fmt(name)
                            : "{0} = {1},".Fmt(name, value));
                    }
                }

                sb = sb.UnIndent();
                sb.AppendLine("End Enum");
            }
            else
            {
                var partial = Config.MakePartial ? "Partial " : "";
                sb.AppendLine("Public {0}Class {1}".Fmt(partial, Type(type.Name, type.GenericArgs)));

                //: BaseClass, Interfaces
                var inheritsList = new List<string>();
                if (type.Inherits != null)
                {
                    inheritsList.Add(Type(type.Inherits, includeNested: true));
                }

                if (options.ImplementsFn != null)
                {
                    var implStr = options.ImplementsFn();
                    if (!string.IsNullOrEmpty(implStr))
                        inheritsList.Add(implStr);
                }

                var makeExtensible = Config.MakeDataContractsExtensible && type.Inherits == null;
                if (makeExtensible)
                    inheritsList.Add("IExtensibleDataObject");
                if (inheritsList.Count > 0)
                    sb.AppendLine("    Inherits {0}".Fmt(string.Join(", ", inheritsList.ToArray())));

                //sb.AppendLine("{");
                sb = sb.Indent();

                AddConstuctor(sb, type, options);
                AddProperties(sb, type);

                foreach (var innerTypeRef in type.InnerTypes.Safe())
                {
                    var innerType = allTypes.FirstOrDefault(x => x.Name == innerTypeRef.Name);
                    if (innerType == null)
                        continue;

                    sb = sb.UnIndent();
                    AppendType(ref sb, innerType, lastNS, allTypes,
                        new CreateTypeOptions { IsNestedType = true });
                    sb = sb.Indent();
                }

                sb = sb.UnIndent();
                sb.AppendLine("End Class");
            }

            sb = sb.UnIndent();
            return lastNS;
        }

        private void AddConstuctor(StringBuilderWrapper sb, MetadataType type, CreateTypeOptions options)
        {
            if (Config.AddImplicitVersion == null && !Config.InitializeCollections)
                return;

            var collectionProps = new List<MetadataPropertyType>();
            if (type.Properties != null && Config.InitializeCollections)
                collectionProps = type.Properties.Where(x => x.IsCollection()).ToList();

            var addVersionInfo = Config.AddImplicitVersion != null && options.IsOperation;
            if (!addVersionInfo && collectionProps.Count <= 0) return;

            if (addVersionInfo)
            {
                var @virtual = Config.MakeVirtual ? "Overridable " : "";
                sb.AppendLine("Public {0}Property Version As Integer".Fmt(@virtual));
                sb.AppendLine();
            }

            sb.AppendLine("Public Sub New()".Fmt(NameOnly(type.Name)));
            //sb.AppendLine("{");
            sb = sb.Indent();

            if (addVersionInfo)
                sb.AppendLine("Version = {0}".Fmt(Config.AddImplicitVersion));

            foreach (var prop in collectionProps)
            {
                sb.AppendLine("{0} = New {1}".Fmt(
                prop.Name.SafeToken(),
                Type(prop.Type, prop.GenericArgs)));
            }

            sb = sb.UnIndent();
            sb.AppendLine("End Sub");
            sb.AppendLine();
        }

        public void AddProperties(StringBuilderWrapper sb, MetadataType type)
        {
            var makeExtensible = Config.MakeDataContractsExtensible && type.Inherits == null;

            var @virtual = Config.MakeVirtual ? "Overridable " : "";
            var wasAdded = false;

            var dataMemberIndex = 1;
            if (type.Properties != null)
            {
                foreach (var prop in type.Properties)
                {
                    if (wasAdded) sb.AppendLine();

                    var propType = Type(prop.Type, prop.GenericArgs);
                    wasAdded = AppendDataMember(sb, prop.DataMember, dataMemberIndex++);
                    wasAdded = AppendAttributes(sb, prop.Attributes) || wasAdded;
                    sb.AppendLine("Public {0}Property {1} As {2}".Fmt(@virtual, prop.Name.SafeToken(), propType));
                }
            }

            if (Config.AddResponseStatus
                && (type.Properties == null
                    || type.Properties.All(x => x.Name != "ResponseStatus")))
            {
                if (wasAdded) sb.AppendLine();
                wasAdded = true;

                AppendDataMember(sb, null, dataMemberIndex++);
                sb.AppendLine("Public {0}Property ResponseStatus As ResponseStatus".Fmt(@virtual));
            }

            if (makeExtensible
                && (type.Properties == null
                    || type.Properties.All(x => x.Name != "ExtensionData")))
            {
                if (wasAdded) sb.AppendLine();
                wasAdded = true;

                sb.AppendLine("Public {0}Property ExtensionData As ExtensionDataObject".Fmt(@virtual));
            }
        }

        public bool AppendAttributes(StringBuilderWrapper sb, List<MetadataAttribute> attributes)
        {
            if (attributes == null || attributes.Count == 0) return false;

            foreach (var attr in attributes)
            {
                if ((attr.Args == null || attr.Args.Count == 0)
                    && (attr.ConstructorArgs == null || attr.ConstructorArgs.Count == 0))
                {
                    sb.AppendLine("<{0}>".Fmt(attr.Name));
                }
                else
                {
                    var args = new StringBuilder();
                    if (attr.ConstructorArgs != null)
                    {
                        foreach (var ctorArg in attr.ConstructorArgs)
                        {
                            if (args.Length > 0)
                                args.Append(", ");
                            args.Append("{0}".Fmt(TypeValue(ctorArg.Type, ctorArg.Value)));
                        }
                    }
                    else if (attr.Args != null)
                    {
                        foreach (var attrArg in attr.Args)
                        {
                            if (args.Length > 0)
                                args.Append(", ");
                            args.Append("{0}:={1}".Fmt(attrArg.Name, TypeValue(attrArg.Type, attrArg.Value)));
                        }
                    }
                    sb.AppendLine("<{0}({1})>".Fmt(attr.Name, args));
                }
            }

            return true;
        }

        public string TypeValue(string type, string value)
        {
            var alias = TypeAlias(type);
            if (value == null)
                return "Nothing";
            if (alias == "String")
                return value.QuotedSafeValue();
            return value;
        }

        public string Type(MetadataTypeName typeName, bool includeNested = false)
        {
            return Type(typeName.Name, typeName.GenericArgs, includeNested: includeNested);
        }

        public string Type(string type, string[] genericArgs, bool includeNested = false)
        {
            if (genericArgs != null)
            {
                if (type == "Nullable`1")
                    return "Nullable(Of {0})".Fmt(TypeAlias(genericArgs[0], includeNested: includeNested));

                var parts = type.Split('`');
                if (parts.Length > 1)
                {
                    var args = new StringBuilder();
                    foreach (var arg in genericArgs)
                    {
                        if (args.Length > 0)
                            args.Append(", ");

                        args.Append(TypeAlias(arg.TrimStart('\''), includeNested: includeNested));
                    }

                    var typeName = NameOnly(type, includeNested: includeNested);
                    return "{0}(Of {1})".Fmt(typeName, args);
                }
            }

            return TypeAlias(type, includeNested: includeNested);
        }

        private string TypeAlias(string type, bool includeNested = false)
        {
            var arrParts = type.SplitOnFirst('[');
            if (arrParts.Length > 1)
                return "{0}()".Fmt(TypeAlias(arrParts[0], includeNested: includeNested));

            return Microsoft.VisualBasic.Information.VbTypeName(type) 
                ?? NameOnly(type, includeNested: includeNested);
        }

        public string NameOnly(string type, bool includeNested = false)
        {
            var name = type.SplitOnFirst('`')[0];

            if (!includeNested)
                name = name.SplitOnLast('.').Last();

            return name.SafeToken();
        }

        public void AppendComments(StringBuilderWrapper sb, string desc)
        {
            if (desc == null) return;

            if (Config.AddDescriptionAsComments)
            {
                sb.AppendLine("'''<Summary>");
                sb.AppendLine("'''{0}".Fmt(desc.SafeComment()));
                sb.AppendLine("'''</Summary>");
            }
            else
            {
                sb.AppendLine("<Description({0})>".Fmt(desc.QuotedSafeValue()));
            }
        }

        public void AppendDataContract(StringBuilderWrapper sb, MetadataDataContract dcMeta)
        {
            if (dcMeta == null)
            {
                if (Config.AddDataContractAttributes)
                    sb.AppendLine("<DataContract>");
                return;
            }

            var dcArgs = "";
            if (dcMeta.Name != null || dcMeta.Namespace != null)
            {
                if (dcMeta.Name != null)
                    dcArgs = "Name:={0}".Fmt(dcMeta.Name.QuotedSafeValue());

                if (dcMeta.Namespace != null)
                {
                    if (dcArgs.Length > 0)
                        dcArgs += ", ";

                    dcArgs += "Namespace:={0}".Fmt(dcMeta.Namespace.QuotedSafeValue());
                }

                dcArgs = "({0})".Fmt(dcArgs);
            }
            sb.AppendLine("<DataContract{0}>".Fmt(dcArgs));
        }

        public bool AppendDataMember(StringBuilderWrapper sb, MetadataDataMember dmMeta, int dataMemberIndex)
        {
            if (dmMeta == null)
            {
                if (Config.AddDataContractAttributes)
                {
                    sb.AppendLine(Config.AddIndexesToDataMembers
                                  ? "<DataMember(Order:={0})>".Fmt(dataMemberIndex)
                                  : "<DataMember>");
                    return true;
                }
                return false;
            }

            var dmArgs = "";
            if (dmMeta.Name != null
                || dmMeta.Order != null
                || dmMeta.IsRequired != null
                || dmMeta.EmitDefaultValue != null
                || Config.AddIndexesToDataMembers)
            {
                if (dmMeta.Name != null)
                    dmArgs = "Name:={0}".Fmt(dmMeta.Name.QuotedSafeValue());

                if (dmMeta.Order != null || Config.AddIndexesToDataMembers)
                {
                    if (dmArgs.Length > 0)
                        dmArgs += ", ";

                    dmArgs += "Order:={0}".Fmt(dmMeta.Order ?? dataMemberIndex);
                }

                if (dmMeta.IsRequired != null)
                {
                    if (dmArgs.Length > 0)
                        dmArgs += ", ";

                    dmArgs += "IsRequired:={0}".Fmt(dmMeta.IsRequired.ToString().ToLower());
                }

                if (dmMeta.EmitDefaultValue != null)
                {
                    if (dmArgs.Length > 0)
                        dmArgs += ", ";

                    dmArgs += "EmitDefaultValue:={0}".Fmt(dmMeta.EmitDefaultValue.ToString().ToLower());
                }

                dmArgs = "({0})".Fmt(dmArgs);
            }
            sb.AppendLine("<DataMember{0}>".Fmt(dmArgs));

            return true;
        }
    }

    public static class VbNetGeneratorExtensions
    {
        public static string SafeComment(this string comment)
        {
            return comment.Replace("\r", "").Replace("\n", "");
        }

        public static string SafeToken(this string token)
        {
            var t = token.Replace("Of ", ""); // remove Of from token so [space] character will work 
            if (t.ContainsAny("\"", " ", "-", "+", "\\", "*", "=", "!"))
                throw new InvalidDataException("MetaData is potentially malicious. Expected token, Received: {0}".Fmt(token));

            return token;
        }

        public static string SafeValue(this string value)
        {
            if (value.Contains('"'))
                throw new InvalidDataException("MetaData is potentially malicious. Expected scalar value, Received: {0}".Fmt(value));

            return value;
        }

        public static string QuotedSafeValue(this string value)
        {
            return "\"{0}\"".Fmt(value.SafeValue());
        }

        public static MetadataAttribute ToMetadataAttribute(this MetadataRoute route)
        {
            var attr = new MetadataAttribute
            {
                Name = "Route",
                ConstructorArgs = new List<MetadataPropertyType>
                {
                    new MetadataPropertyType { Type = "String", Value = route.Path },
                },
            };

            if (route.Verbs != null)
            {
                attr.ConstructorArgs.Add(
                    new MetadataPropertyType { Type = "String", Value = route.Verbs });
            }

            return attr;
        }
    }
}