using Iron.ProcessMaps;

// The process maps' check (ADR-0045): every pack under the folder it is given (make prd-check gives docs/prd) has its BPMN
// maps, and each map is valid against OMG's BPMN 2.0 schema with sound references, lanes, diagram and step names. Exit
// code 1 when a pack has no map or a map has a problem.
var root = args.Length > 0 ? args[0] : Path.Combine("docs", "prd");
var schemas = ProcessMapCheck.LoadSchemas(Path.Combine(AppContext.BaseDirectory, "schema"));
var problems = 0;
foreach (var (pack, maps) in ProcessMapCheck.FindPacks(root))
{
    var shown = Path.GetRelativePath(root, pack).Replace('\\', '/');
    if (maps.Count == 0)
    {
        Console.WriteLine($"{shown}: no BPMN maps in the folder {Path.GetFileNameWithoutExtension(pack)}/ beside it (PM-001)");
        problems++;
        continue;
    }

    foreach (var map in maps)
    {
        var found = ProcessMapCheck.Check(map, schemas);
        foreach (var problem in found)
        {
            Console.WriteLine($"{Path.GetRelativePath(root, Path.GetDirectoryName(map)!).Replace('\\', '/')}/{problem}");
        }

        problems += found.Count;
    }

    Console.WriteLine($"{shown,-60} {maps.Count} BPMN maps checked");
}

Console.WriteLine(problems == 0 ? "process-maps: passed" : $"\nprocess-maps: {problems} problems");
return problems == 0 ? 0 : 1;
