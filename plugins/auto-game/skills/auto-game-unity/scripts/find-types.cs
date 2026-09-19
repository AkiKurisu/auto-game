AutoGameValue queryValue;
AutoGameValue limitValue;
var query = (Args.TryGetValue("query", out queryValue) ? queryValue.AsString() : "").Trim();
var limit = Math.Max(1, Math.Min(500, Args.TryGetValue("limit", out limitValue) ? (int)limitValue.AsInt64() : 100));
if (query.Length == 0) return new { error = "args.query is required" };
return AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(assembly => { try { return assembly.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(type => type != null); } })
    .Where(type => type != null && type.FullName != null && type.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
    .Take(limit)
    .Select(type => new { type = type.FullName, assembly = type.Assembly.GetName().Name })
    .ToArray();
