using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Iron.ProcessMaps;

/// <summary>
/// Checks the BPMN maps of the process reference packs (ADR-0045). First the file against OMG's BPMN 2.0 schema; then what
/// the schema cannot see, because BPMN's references are names rather than XML identifiers: every reference names an
/// element of the file, every flow node stands in exactly one lane, every element has its place on the diagram so a BPMN
/// tool can draw it, and every task starts its name with its step identifier, as the pack's requirements cite it.
/// </summary>
public static partial class ProcessMapCheck
{
    /// <summary>The folder a pack's maps live in, beside the pack: process-reference/supplier-management/.</summary>
    public const string PackFolder = "process-reference";

    private static readonly XNamespace Model = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Diagram = "http://www.omg.org/spec/BPMN/20100524/DI";

    private static readonly HashSet<string> Tasks =
        ["task", "userTask", "serviceTask", "manualTask", "sendTask", "receiveTask", "businessRuleTask", "scriptTask"];

    private static readonly HashSet<string> FlowNodes =
    [
        .. Tasks, "subProcess", "callActivity", "startEvent", "endEvent", "intermediateCatchEvent", "intermediateThrowEvent",
        "boundaryEvent", "exclusiveGateway", "parallelGateway", "inclusiveGateway", "eventBasedGateway", "complexGateway",
    ];

    // Attributes and child elements whose value names another element of the file.
    private static readonly HashSet<string> ReferenceAttributes = ["sourceRef", "targetRef", "processRef", "bpmnElement", "attachedToRef", "default"];
    private static readonly HashSet<string> ReferenceElements = ["flowNodeRef", "incoming", "outgoing"];

    /// <summary>OMG's schema set from the folder that holds BPMN20.xsd and the files it imports.</summary>
    public static XmlSchemaSet LoadSchemas(string folder)
    {
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(Model.NamespaceName, Path.Combine(folder, "BPMN20.xsd"));
        schemas.Compile();
        return schemas;
    }

    /// <summary>The packs under a folder of requirement documents, each with the BPMN files of its folder.</summary>
    public static IReadOnlyList<(string Pack, IReadOnlyList<string> Maps)> FindPacks(string root) =>
        [.. Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == PackFolder)
            .Order(StringComparer.Ordinal)
            .Select(pack =>
            {
                var folder = Path.Combine(Path.GetDirectoryName(pack)!, Path.GetFileNameWithoutExtension(pack));
                IReadOnlyList<string> maps = Directory.Exists(folder)
                    ? [.. Directory.EnumerateFiles(folder, "*.bpmn").Order(StringComparer.Ordinal)]
                    : [];
                return (pack, maps);
            })];

    /// <summary>Every problem of one map, each with its file and line; empty when the map is sound.</summary>
    public static IReadOnlyList<string> Check(string path, XmlSchemaSet schemas)
    {
        var name = Path.GetFileName(path);
        var problems = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas, DtdProcessing = DtdProcessing.Prohibit };
        settings.ValidationEventHandler += (_, e) => problems.Add($"{name}:{e.Exception?.LineNumber}: {e.Message}");
        try
        {
            using var reader = XmlReader.Create(path, settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException e)
        {
            return [$"{name}:{e.LineNumber}: not well-formed XML: {e.Message}"];
        }

        // The checks below read a file the schema accepts; on a file it refuses they would only repeat its findings.
        if (problems.Count > 0)
        {
            return problems;
        }

        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        var elements = document.Descendants().Where(e => e.Attribute("id") is not null).ToDictionary(e => (string)e.Attribute("id")!, StringComparer.Ordinal);
        problems.AddRange(References(document, elements).Select(problem => $"{name}:{problem}"));
        problems.AddRange(Lanes(document).Select(problem => $"{name}:{problem}"));
        problems.AddRange(DiagramPlaces(document, elements).Select(problem => $"{name}:{problem}"));
        problems.AddRange(StepNames(document).Select(problem => $"{name}:{problem}"));
        return problems;
    }

    private static IEnumerable<string> References(XDocument document, Dictionary<string, XElement> elements)
    {
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => ReferenceAttributes.Contains(a.Name.LocalName)))
            {
                if (!elements.ContainsKey(LocalName(attribute.Value)))
                {
                    yield return $"{Line(element)}: {element.Name.LocalName} {Id(element)} has {attribute.Name.LocalName} '{attribute.Value}', which names no element of the file";
                }
            }

            if (ReferenceElements.Contains(element.Name.LocalName) && !elements.ContainsKey(LocalName(element.Value.Trim())))
            {
                yield return $"{Line(element)}: {element.Name.LocalName} '{element.Value.Trim()}' names no element of the file";
            }
        }
    }

    private static IEnumerable<string> Lanes(XDocument document)
    {
        foreach (var process in document.Descendants(Model + "process").Where(p => p.Descendants(Model + "lane").Any()))
        {
            var placed = process.Descendants(Model + "flowNodeRef").GroupBy(r => LocalName(r.Value.Trim()), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var node in process.Elements().Where(e => FlowNodes.Contains(e.Name.LocalName)))
            {
                var count = placed.GetValueOrDefault(Id(node));
                if (count != 1)
                {
                    yield return $"{Line(node)}: {node.Name.LocalName} {Id(node)} stands in {count} lanes; every flow node stands in exactly one";
                }
            }
        }
    }

    private static IEnumerable<string> DiagramPlaces(XDocument document, Dictionary<string, XElement> elements)
    {
        var drawn = document.Descendants().Where(e => e.Name.Namespace == Diagram).Select(e => (string?)e.Attribute("bpmnElement")).OfType<string>().Select(LocalName).ToHashSet(StringComparer.Ordinal);
        if (drawn.Count == 0)
        {
            yield return "1: no diagram (bpmndi:BPMNDiagram); a BPMN tool would have nothing to draw";
            yield break;
        }

        var placed = new[] { "lane", "participant", "sequenceFlow", "messageFlow", "association", "textAnnotation" };
        foreach (var element in elements.Values.Where(e => e.Name.Namespace == Model && (FlowNodes.Contains(e.Name.LocalName) || placed.Contains(e.Name.LocalName))))
        {
            if (!drawn.Contains(Id(element)))
            {
                yield return $"{Line(element)}: {element.Name.LocalName} {Id(element)} has no shape or edge on the diagram";
            }
        }
    }

    private static IEnumerable<string> StepNames(XDocument document)
    {
        foreach (var task in document.Descendants().Where(e => e.Name.Namespace == Model && Tasks.Contains(e.Name.LocalName)))
        {
            if (!StepName().IsMatch((string?)task.Attribute("name") ?? ""))
            {
                yield return $"{Line(task)}: task {Id(task)} is named '{(string?)task.Attribute("name")}'; a task's name starts with its step identifier (SM-01, E1)";
            }
        }
    }

    // A step identifier: two or three capitals, a hyphen and two digits (SM-01, CM-07), or one capital and a number (E1, C6).
    [GeneratedRegex(@"^(?:[A-Z]{2,3}-\d{2}|[A-Z]\d{1,2}) \S")]
    private static partial Regex StepName();

    private static string LocalName(string reference) => reference[(reference.IndexOf(':', StringComparison.Ordinal) + 1)..];

    private static string Id(XElement element) => (string?)element.Attribute("id") ?? "(no id)";

    private static int Line(XElement element) => ((IXmlLineInfo)element).LineNumber;
}
