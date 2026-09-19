using System.Reflection;
var name = Args.GetString("type");
var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null);
if (type == null) return new { error = "type not found", type = name };
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
return new {
    type = type.FullName,
    assembly = type.Assembly.GetName().Name,
    baseType = type.BaseType == null ? null : type.BaseType.FullName,
    constructors = type.GetConstructors(flags).Select(value => value.ToString()).ToArray(),
    methods = type.GetMethods(flags).Select(value => value.ToString()).OrderBy(value => value).ToArray(),
    properties = type.GetProperties(flags).Select(value => value.ToString()).OrderBy(value => value).ToArray(),
    fields = type.GetFields(flags).Select(value => value.FieldType.FullName + " " + value.Name).OrderBy(value => value).ToArray()
};
