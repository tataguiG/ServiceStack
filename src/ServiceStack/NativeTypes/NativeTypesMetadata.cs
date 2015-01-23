﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ServiceStack.Auth;
using ServiceStack.Host;
using ServiceStack.Text;
using ServiceStack.Web;

namespace ServiceStack.NativeTypes
{
    public class NativeTypesMetadata : INativeTypesMetadata
    {
        private readonly ServiceMetadata meta;
        private readonly MetadataTypesConfig defaults;

        public NativeTypesMetadata(ServiceMetadata meta, MetadataTypesConfig defaults)
        {
            this.meta = meta;
            this.defaults = defaults;
        }

        public MetadataTypesConfig GetConfig(NativeTypesBase req)
        {
            return new MetadataTypesConfig
            {
                BaseUrl = req.BaseUrl ?? defaults.BaseUrl,
                MakePartial = req.MakePartial ?? defaults.MakePartial,
                MakeVirtual = req.MakeVirtual ?? defaults.MakeVirtual,
                AddReturnMarker = req.AddReturnMarker ?? defaults.AddReturnMarker,
                AddDescriptionAsComments = req.AddDescriptionAsComments ?? defaults.AddDescriptionAsComments,
                AddDataContractAttributes = req.AddDataContractAttributes ?? defaults.AddDataContractAttributes,
                MakeDataContractsExtensible = req.MakeDataContractsExtensible ?? defaults.MakeDataContractsExtensible,
                AddIndexesToDataMembers = req.AddIndexesToDataMembers ?? defaults.AddIndexesToDataMembers,
                InitializeCollections = req.InitializeCollections ?? defaults.InitializeCollections,
                AddImplicitVersion = req.AddImplicitVersion ?? defaults.AddImplicitVersion,
                AddResponseStatus = req.AddResponseStatus ?? defaults.AddResponseStatus,
                AddServiceStackTypes = req.AddServiceStackTypes ?? defaults.AddServiceStackTypes,
                AddModelExtensions = req.AddModelExtensions ?? defaults.AddModelExtensions,
                MakePropertiesOptional = req.MakePropertiesOptional ?? defaults.MakePropertiesOptional,
                AddDefaultXmlNamespace = req.AddDefaultXmlNamespace ?? defaults.AddDefaultXmlNamespace,
                DefaultNamespaces = req.DefaultNamespaces ?? defaults.DefaultNamespaces,
                ExportAttributes = defaults.ExportAttributes,
                IgnoreTypes = defaults.IgnoreTypes,
                IgnoreTypesInNamespaces = defaults.IgnoreTypesInNamespaces,
                CSharpTypeAlias = defaults.CSharpTypeAlias,
                FSharpTypeAlias = defaults.FSharpTypeAlias,
                VbNetTypeAlias = defaults.VbNetTypeAlias,
                TypeScriptTypeAlias = defaults.TypeScriptTypeAlias,
                SwiftTypeAlias = defaults.SwiftTypeAlias,
                VbNetKeyWords = defaults.VbNetKeyWords,
                GlobalNamespace = req.GlobalNamespace ?? defaults.GlobalNamespace,
            };
        }

        public MetadataTypes GetMetadataTypes(IRequest req, MetadataTypesConfig config = null)
        {
            return GetMetadataTypesGenerator(config).GetMetadataTypes(req);
        }

        internal MetadataTypesGenerator GetMetadataTypesGenerator(MetadataTypesConfig config)
        {
            return new MetadataTypesGenerator(meta, config ?? defaults);
        }
    }

    public class MetadataTypesGenerator
    {
        private readonly ServiceMetadata meta;
        private readonly MetadataTypesConfig config;

        public MetadataTypesGenerator(ServiceMetadata meta, MetadataTypesConfig config)
        {
            this.meta = meta;
            this.config = config;
        }

        public MetadataTypes GetMetadataTypes(IRequest req)
        {
            var metadata = new MetadataTypes
            {
                Config = config,
            };

            var skipTypes = config.IgnoreTypes ?? new HashSet<Type>();
            var opTypes = new HashSet<Type>();
            var ignoreNamespaces = config.IgnoreTypesInNamespaces ?? new List<string>();

            foreach (var operation in meta.Operations)
            {
                if (!meta.IsVisible(req, operation))
                    continue;

                if (opTypes.Contains(operation.RequestType))
                    continue;

                if (skipTypes.Contains(operation.RequestType))
                    continue;

                if (ignoreNamespaces.Contains(operation.RequestType.Namespace))
                    continue;

                var opType = new MetadataOperationType
                {
                    Actions = operation.Actions,
                    Request = ToType(operation.RequestType),
                    Response = ToType(operation.ResponseType),
                };
                metadata.Operations.Add(opType);
                opTypes.Add(operation.RequestType);
                
                if (operation.ResponseType != null)
                {
                    if (skipTypes.Contains(operation.ResponseType))
                    {
                        //Config.IgnoreTypesInNamespaces in CSharpGenerator
                        opType.Response = null;
                    }
                    else
                    {
                        opTypes.Add(operation.ResponseType);
                    }
                }
            }

            var considered = new HashSet<Type>(opTypes);
            var queue = new Queue<Type>(opTypes);

            Func<Type, bool> ignoreTypeFn = t => 
                t == null
                || t.IsGenericParameter
                || considered.Contains(t)
                || skipTypes.Contains(t)
                || ignoreNamespaces.Contains(t.Namespace);

            Action<Type> registerTypeFn = null;
            registerTypeFn = t => {
                if (t.IsArray || t == typeof(Array))
                    return;

                considered.Add(t);
                queue.Enqueue(t);

                if (!t.IsSystemType() 
                    && (t.IsClass || t.IsEnum || t.IsInterface)
                    && !(t.IsGenericParameter))
                {
                    metadata.Types.Add(ToType(t));

                    foreach (var ns in GetNamespacesUsed(t))
                    {
                        if (!metadata.Namespaces.Contains(ns))
                            metadata.Namespaces.Add(ns);
                    }
                }
            };

            while (queue.Count > 0)
            {
                var type = queue.Dequeue();

                if (IsSystemCollection(type))
                {
                    type = type.GetCollectionType();
                    if (type != null && !ignoreTypeFn(type)) 
                        registerTypeFn(type);
                    continue;
                }

                if (type.DeclaringType != null)
                {
                    if (!ignoreTypeFn(type.DeclaringType))
                        registerTypeFn(type.DeclaringType);
                }

                if (!type.IsUserType() && !type.IsInterface) 
                    continue;

                foreach (var pi in type.GetSerializableProperties()
                    .Where(pi => !ignoreTypeFn(pi.PropertyType)))
                {
                    registerTypeFn(pi.PropertyType);

                    //Register Property Array Element Types 
                    if (pi.PropertyType.IsArray && !ignoreTypeFn(pi.PropertyType.GetElementType()))
                    {
                        registerTypeFn(pi.PropertyType.GetElementType());
                    }

                    //Register Property Generic Arg Types 
                    if (!pi.PropertyType.IsGenericType()) continue;
                    var propArgs = pi.PropertyType.GetGenericArguments();
                    foreach (var arg in propArgs.Where(arg => !ignoreTypeFn(arg)))
                    {
                        registerTypeFn(arg);
                    }
                }

                if (!ignoreTypeFn(type.BaseType))
                {
                    if (type.BaseType.IsGenericType)
                    {
                        var genericDef = type.BaseType.GetGenericTypeDefinition();
                        if (!ignoreTypeFn(genericDef))
                            registerTypeFn(genericDef);
                        
                        foreach (var arg in type.BaseType.GetGenericArguments()
                            .Where(arg => !ignoreTypeFn(arg)))
                        {
                            registerTypeFn(arg);
                        }
                    }
                    else
                    {
                        registerTypeFn(type.BaseType);
                    }
                }

                if (!type.IsGenericType()) 
                    continue;

                //Register Generic Arg Types 
                var args = type.GetGenericArguments();
                foreach (var arg in args.Where(arg => !ignoreTypeFn(arg)))
                {
                    registerTypeFn(arg);
                }
            }

            return metadata;
        }

        private static bool IsSystemCollection(Type type)
        {
            return type.IsArray 
                || (type.Namespace != null
                    && type.Namespace.StartsWith("System")
                    && type.IsOrHasGenericInterfaceTypeOf(typeof (IEnumerable<>)));
        }

        public MetadataTypeName ToTypeName(Type type)
        {
            if (type == null) return null;

            return new MetadataTypeName
            {
                Name = type.GetOperationName(),
                Namespace = type.Namespace,
                GenericArgs = type.IsGenericType
                    ? type.GetGenericArguments().Select(x => x.GetOperationName()).ToArray()
                    : null,
            };
        }

        public MetadataType ToType(Type type)
        {
            if (type == null) return null;

            var metaType = new MetadataType
            {
                Name = type.GetOperationName(),
                Namespace = type.Namespace,
                GenericArgs = type.IsGenericType
                    ? type.GetGenericArguments().Select(x => x.GetOperationName()).ToArray()
                    : null,
                Attributes = ToAttributes(type),
                Properties = ToProperties(type),
                IsNested = type.IsNested ? true : (bool?)null,
                IsEnum = type.IsEnum ? true : (bool?)null,
                IsInterface = type.IsInterface ? true : (bool?)null,
            };

            if (type.BaseType != null && type.BaseType != typeof(object) && !type.IsEnum)
            {
                metaType.Inherits = ToTypeName(type.BaseType);
            }

            if (type.GetTypeWithInterfaceOf(typeof(IReturnVoid)) != null)
            {
                metaType.ReturnVoidMarker = true;
            }
            else
            {
                var genericMarker = type.GetTypeWithGenericTypeDefinitionOf(typeof(IReturn<>));
                if (genericMarker != null)
                {
                    metaType.ReturnMarkerTypeName = ToTypeName(genericMarker.GetGenericArguments().First());
                }
            }

            var routeAttrs = HostContext.AppHost.GetRouteAttributes(type).ToList();
            if (routeAttrs.Count > 0)
            {
                metaType.Routes = routeAttrs.ConvertAll(x =>
                    new MetadataRoute
                    {
                        Path = x.Path,
                        Notes = x.Notes,
                        Summary = x.Summary,
                        Verbs = x.Verbs,
                    });
            }

            metaType.Description = type.GetDescription();

            var dcAttr = type.GetDataContract();
            if (dcAttr != null)
            {
                metaType.DataContract = new MetadataDataContract
                {
                    Name = dcAttr.Name,
                    Namespace = dcAttr.Namespace,
                };
            }

            if (type.IsEnum)
            {
                metaType.EnumNames = new List<string>();
                metaType.EnumValues = new List<string>();

                var isDefaultLayout = true;
                var values = Enum.GetValues(type);
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values.GetValue(i);
                    var name = value.ToString();
                    var enumValue = Convert.ChangeType(value, Type.GetTypeCode(type)).ToString();

                    if (enumValue != i.ToString())
                        isDefaultLayout = false;

                    metaType.EnumNames.Add(name);
                    metaType.EnumValues.Add(enumValue);
                }

                if (isDefaultLayout)
                    metaType.EnumValues = null; 
            }

            var innerTypes = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var innerType in innerTypes)
            {
                if (metaType.InnerTypes == null)
                    metaType.InnerTypes = new List<MetadataTypeName>();

                metaType.InnerTypes.Add(new MetadataTypeName
                {
                    Name = innerType.GetOperationName(),
                    Namespace = innerType.Namespace,
                    GenericArgs = innerType.IsGenericType
                        ? innerType.GetGenericArguments().Select(x => x.GetOperationName()).ToArray()
                        : null,
                });
            }

            return metaType;
        }

        public List<MetadataAttribute> ToAttributes(Type type)
        {
            return !(type.IsUserType() || type.IsUserEnum() || type.IsInterface) || type.IsOrHasGenericInterfaceTypeOf(typeof(IEnumerable<>))
                ? null
                : ToAttributes(type.GetCustomAttributes(false));
        }

        public List<MetadataPropertyType> ToProperties(Type type)
        {
            var props = (!type.IsUserType() && !type.IsInterface) || type.IsOrHasGenericInterfaceTypeOf(typeof(IEnumerable<>))
                ? null
                : GetInstancePublicProperties(type).Select(x => ToProperty(x)).ToList();

            return props == null || props.Count == 0 ? null : props;
        }

        public HashSet<string> GetNamespacesUsed(Type type)
        {
            var to = new HashSet<string>();

            if (type.IsUserType() || type.IsInterface || type.IsOrHasGenericInterfaceTypeOf(typeof(IEnumerable<>)))
            {
                foreach (var pi in GetInstancePublicProperties(type))
                {
                    if (pi.PropertyType.Namespace != null)
                    {
                        to.Add(pi.PropertyType.Namespace);
                    }

                    if (pi.PropertyType.IsGenericType)
                    {
                        pi.PropertyType.GetGenericArguments()
                            .Where(x => x.Namespace != null).Each(x => to.Add(x.Namespace));
                    }
                }

                if (type.IsGenericType)
                {                   
                    type.GetGenericArguments()
                        .Where(x => x.Namespace != null).Each(x => to.Add(x.Namespace));
                }
            }

            if (type.Namespace != null)
            {
                to.Add(type.Namespace);
            }

            return to;
        }

        public bool IncludeAttrsFilter(Attribute x)
        {
            var type = x.GetType();
            return config.ExportAttributes.Contains(type);
        }

        public List<MetadataAttribute> ToAttributes(object[] attrs)
        {
            var to = attrs.OfType<Attribute>()
                .Where(IncludeAttrsFilter)
                .Select(ToAttribute)
                .ToList();

            return to.Count == 0 ? null : to;
        }

        public List<MetadataAttribute> ToAttributes(IEnumerable<Attribute> attrs)
        {
            var to = attrs
                .Where(IncludeAttrsFilter)
                .Select(ToAttribute)
                .ToList();

            return to.Count == 0 ? null : to;
        }

        public MetadataAttribute ToAttribute(Attribute attr)
        {
            var firstCtor = attr.GetType().GetConstructors()
                //.OrderBy(x => x.GetParameters().Length)
                .FirstOrDefault();
            var metaAttr = new MetadataAttribute
            {
                Name = attr.GetType().Name.Replace("Attribute", ""),
                ConstructorArgs = firstCtor != null
                    ? firstCtor.GetParameters().ToList().ConvertAll(ToProperty)
                    : null,
                Args = NonDefaultProperties(attr),
            };

            //Populate ctor Arg values from matching properties
            var argValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metaAttr.Args.Each(x => argValues[x.Name] = x.Value);
            metaAttr.Args.RemoveAll(x => x.ReadOnly == true);

            if (metaAttr.ConstructorArgs != null)
            {
                foreach (var arg in metaAttr.ConstructorArgs)
                {
                    string value;
                    if (argValues.TryGetValue(arg.Name, out value))
                    {
                        arg.Value = value;
                    }
                }
                metaAttr.ConstructorArgs.RemoveAll(x => x.Value == null);
                if (metaAttr.ConstructorArgs.Count == 0)
                    metaAttr.ConstructorArgs = null;
            }

            //Only emit ctor args or property args
            if (metaAttr.ConstructorArgs == null 
                || metaAttr.ConstructorArgs.Count != metaAttr.Args.Count)
            {
                metaAttr.ConstructorArgs = null;
            }
            else
            {
                metaAttr.Args = null;
            }

            return metaAttr;
        }

        public List<MetadataPropertyType> NonDefaultProperties(Attribute attr)
        {
            return attr.GetType().GetPublicProperties()
                .Select(pi => ToProperty(pi, attr))
                .Where(property => property.Name != "TypeId"
                    && property.Value != null)
                .ToList();
        }

        public MetadataPropertyType ToProperty(PropertyInfo pi, object instance = null)
        {
            var property = new MetadataPropertyType
            {
                Name = pi.Name,
                Attributes = ToAttributes(pi.GetCustomAttributes(false)),
                Type = pi.PropertyType.GetOperationName(),
                IsValueType = pi.PropertyType.IsValueType ? true : (bool?) null,
                TypeNamespace = pi.PropertyType.Namespace,
                DataMember = ToDataMember(pi.GetDataMember()),
                GenericArgs = pi.PropertyType.IsGenericType
                    ? pi.PropertyType.GetGenericArguments().Select(x => x.GetOperationName()).ToArray()
                    : null,
                Description = pi.GetDescription(),
            };

            var apiMember = pi.FirstAttribute<ApiMemberAttribute>();
            if (apiMember != null)
            {
                if (apiMember.IsRequired)
                    property.IsRequired = true;

                property.ParamType = apiMember.ParameterType;
                property.DisplayType = apiMember.DataType;
            }

            var apiAllowableValues = pi.FirstAttribute<ApiAllowableValuesAttribute>();
            if (apiAllowableValues != null)
            {
                property.AllowableValues = apiAllowableValues.Values;
                property.AllowableMin = apiAllowableValues.Min;
                property.AllowableMax = apiAllowableValues.Max;
            }

            if (instance != null)
            {
                var value = pi.GetValue(instance, null);
                if (value != null
                    && !value.Equals(pi.PropertyType.GetDefaultValue()))
                {
                    if (pi.PropertyType.IsEnum())
                    {
                        property.Value = "{0}.{1}".Fmt(pi.PropertyType.Name, value);
                    }
                    else if (pi.PropertyType == typeof(Type))
                    {
                        var type = (Type)value;
                        property.Value = "typeof({0})".Fmt(type.FullName);
                    }
                    else
                    {
                        var strValue = value as string;
                        property.Value = strValue ?? value.ToJson();
                    }
                }

                if (pi.GetSetMethod() == null) //ReadOnly is bool? to minimize serialization
                    property.ReadOnly = true;
            }
            return property;
        }

        public MetadataPropertyType ToProperty(ParameterInfo pi)
        {
            var propertyAttrs = pi.AllAttributes();
            var property = new MetadataPropertyType
            {
                Name = pi.Name,
                Attributes = ToAttributes(propertyAttrs),
                Type = pi.ParameterType.GetOperationName(),
                IsValueType = pi.ParameterType.IsValueType ? true : (bool?)null,
                TypeNamespace = pi.ParameterType.Namespace,
                Description = pi.GetDescription(),
            };

            return property;
        }

        public static MetadataDataMember ToDataMember(DataMemberAttribute attr)
        {
            if (attr == null) return null;

            var metaAttr = new MetadataDataMember
            {
                Name = attr.Name,
                EmitDefaultValue = attr.EmitDefaultValue != true ? attr.EmitDefaultValue : (bool?)null,
                Order = attr.Order >= 0 ? attr.Order : (int?)null,
                IsRequired = attr.IsRequired != false ? attr.IsRequired : (bool?)null,
            };

            return metaAttr;
        }

        public static PropertyInfo[] GetInstancePublicProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(t => t.GetIndexParameters().Length == 0) // ignore indexed properties
                .ToArray();
        }
    }

    public static class MetadataExtensions
    {
        public static bool IgnoreSystemType(this MetadataType type)
        {
            return type == null
                || (type.Namespace != null && type.Namespace.StartsWith("System"))
                || (type.Inherits != null && type.Inherits.Name == "Array");
        }

        public static HashSet<string> GetDefaultNamespaces(this MetadataTypesConfig config, MetadataTypes metadata)
        {
            var namespaces = config.DefaultNamespaces.ToHashSet();

            //Add any ignored namespaces used
            foreach (var ns in metadata.Namespaces)
            {
                //Ignored by IsUserType()
                if (!ns.StartsWith("System") && !config.IgnoreTypesInNamespaces.Contains(ns)) 
                    continue;
                
                if (!namespaces.Contains(ns))
                {
                    namespaces.Add(ns);
                }
            }

            return namespaces;
        }
    }
}