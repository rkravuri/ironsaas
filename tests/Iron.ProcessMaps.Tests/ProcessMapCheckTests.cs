using System.Xml.Schema;
using Iron.ProcessMaps;

namespace Iron.ProcessMaps.Tests;

/// <summary>The process maps' check: a sound map passes, each rule broken once is named, and the packs' maps pass.</summary>
public sealed class ProcessMapCheckTests : IDisposable
{
    // A small map that keeps every rule: one pool, two lanes, a start, a task, a gateway and two ends, all on the diagram.
    private const string Sound = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                     xmlns:dc="http://www.omg.org/spec/DD/20100524/DC" xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                     id="probe" targetNamespace="https://ironsaas.example/probe">
          <collaboration id="collaboration">
            <participant id="pool" name="Probe" processRef="process"/>
          </collaboration>
          <process id="process" isExecutable="false">
            <laneSet id="lanes">
              <lane id="lane-requester" name="Requester">
                <flowNodeRef>start</flowNodeRef>
                <flowNodeRef>SM-01</flowNodeRef>
              </lane>
              <lane id="lane-buyer" name="Buyer">
                <flowNodeRef>SM-02</flowNodeRef>
                <flowNodeRef>end-new</flowNodeRef>
                <flowNodeRef>end-known</flowNodeRef>
              </lane>
            </laneSet>
            <startEvent id="start" name="A supplier is needed"/>
            <task id="SM-01" name="SM-01 Request a new supplier"/>
            <exclusiveGateway id="SM-02" name="SM-02 Duplicate or existing supplier?"/>
            <endEvent id="end-new" name="Invited"/>
            <endEvent id="end-known" name="Enabled"/>
            <sequenceFlow id="flow-1" sourceRef="start" targetRef="SM-01"/>
            <sequenceFlow id="flow-2" sourceRef="SM-01" targetRef="SM-02"/>
            <sequenceFlow id="flow-3" name="no" sourceRef="SM-02" targetRef="end-new"/>
            <sequenceFlow id="flow-4" name="yes" sourceRef="SM-02" targetRef="end-known"/>
          </process>
          <bpmndi:BPMNDiagram id="diagram">
            <bpmndi:BPMNPlane id="plane" bpmnElement="collaboration">
              <bpmndi:BPMNShape id="pool-shape" bpmnElement="pool" isHorizontal="true"><dc:Bounds x="0" y="0" width="600" height="280"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="lane-requester-shape" bpmnElement="lane-requester" isHorizontal="true"><dc:Bounds x="30" y="0" width="570" height="140"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="lane-buyer-shape" bpmnElement="lane-buyer" isHorizontal="true"><dc:Bounds x="30" y="140" width="570" height="140"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="start-shape" bpmnElement="start"><dc:Bounds x="80" y="52" width="36" height="36"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="SM-01-shape" bpmnElement="SM-01"><dc:Bounds x="160" y="30" width="120" height="80"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="SM-02-shape" bpmnElement="SM-02" isMarkerVisible="true"><dc:Bounds x="330" y="185" width="50" height="50"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="end-new-shape" bpmnElement="end-new"><dc:Bounds x="450" y="160" width="36" height="36"/></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="end-known-shape" bpmnElement="end-known"><dc:Bounds x="450" y="230" width="36" height="36"/></bpmndi:BPMNShape>
              <bpmndi:BPMNEdge id="flow-1-edge" bpmnElement="flow-1"><di:waypoint x="116" y="70"/><di:waypoint x="160" y="70"/></bpmndi:BPMNEdge>
              <bpmndi:BPMNEdge id="flow-2-edge" bpmnElement="flow-2"><di:waypoint x="280" y="70"/><di:waypoint x="355" y="185"/></bpmndi:BPMNEdge>
              <bpmndi:BPMNEdge id="flow-3-edge" bpmnElement="flow-3"><di:waypoint x="380" y="210"/><di:waypoint x="450" y="178"/></bpmndi:BPMNEdge>
              <bpmndi:BPMNEdge id="flow-4-edge" bpmnElement="flow-4"><di:waypoint x="380" y="210"/><di:waypoint x="450" y="248"/></bpmndi:BPMNEdge>
            </bpmndi:BPMNPlane>
          </bpmndi:BPMNDiagram>
        </definitions>
        """;

    private static readonly XmlSchemaSet Schemas = ProcessMapCheck.LoadSchemas(Path.Combine(AppContext.BaseDirectory, "schema"));

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("process-maps-");

    public void Dispose() => _folder.Delete(recursive: true);

    private IReadOnlyList<string> CheckMap(string text)
    {
        var path = Path.Combine(_folder.FullName, "probe.bpmn");
        File.WriteAllText(path, text);
        return ProcessMapCheck.Check(path, Schemas);
    }

    [Fact]
    [Proves("ADR-0045")]
    public void ASoundMapPasses() => Assert.Empty(CheckMap(Sound));

    [Fact]
    [Proves("ADR-0045")]
    public void AMapTheSchemaRefusesIsNamedWithItsLine()
    {
        var problems = CheckMap(Sound.Replace("""<task id="SM-01" name="SM-01 Request a new supplier"/>""", """<task id="SM-01" name="SM-01 Request a new supplier" colour="red"/>""", StringComparison.Ordinal));

        var problem = Assert.Single(problems);
        Assert.StartsWith("probe.bpmn:21:", problem, StringComparison.Ordinal);
        Assert.Contains("'colour' attribute is not allowed", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0045")]
    public void AFileThatIsNotWellFormedIsRefused()
    {
        var problem = Assert.Single(CheckMap(Sound.Replace("</process>", "", StringComparison.Ordinal)));

        Assert.Contains("not well-formed XML", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0045")]
    public void AFlowWhoseTargetNamesNothingIsRefusedByTheSchema()
    {
        // BPMN declares a sequence flow's ends as XML references, so the schema itself finds this one.
        var problems = CheckMap(Sound.Replace("""targetRef="end-known"/>""", """targetRef="end-lost"/>""", StringComparison.Ordinal));

        Assert.Contains(problems, p => p.Contains("Reference to undeclared ID is 'end-lost'", StringComparison.Ordinal));
    }

    [Fact]
    [Proves("ADR-0045")]
    public void AShapeThatNamesNothingIsRefused()
    {
        // A diagram's references are names the schema cannot resolve; the check does.
        var problems = CheckMap(Sound.Replace("""bpmnElement="end-known">""", """bpmnElement="end-lost">""", StringComparison.Ordinal));

        Assert.Contains(problems, p => p.Contains("BPMNShape end-known-shape has bpmnElement 'end-lost', which names no element of the file", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("endEvent end-known has no shape or edge on the diagram", StringComparison.Ordinal));
    }

    [Fact]
    [Proves("ADR-0045")]
    public void AFlowNodeOutsideEveryLaneIsRefused()
    {
        var problems = CheckMap(Sound.Replace("<flowNodeRef>end-known</flowNodeRef>", "", StringComparison.Ordinal));

        Assert.Contains(problems, p => p.Contains("endEvent end-known stands in 0 lanes; every flow node stands in exactly one", StringComparison.Ordinal));
    }

    [Fact]
    [Proves("ADR-0045")]
    public void AnElementWithoutItsPlaceOnTheDiagramIsRefused()
    {
        var problems = CheckMap(Sound.Replace("""<bpmndi:BPMNEdge id="flow-4-edge" bpmnElement="flow-4"><di:waypoint x="380" y="210"/><di:waypoint x="450" y="248"/></bpmndi:BPMNEdge>""", "", StringComparison.Ordinal));

        Assert.Contains(problems, p => p.Contains("sequenceFlow flow-4 has no shape or edge on the diagram", StringComparison.Ordinal));
    }

    [Fact]
    [Proves("ADR-0045")]
    public void ATaskNotNamedByItsStepIsRefused()
    {
        var problems = CheckMap(Sound.Replace("SM-01 Request a new supplier", "Request a new supplier", StringComparison.Ordinal));

        Assert.Contains(problems, p => p.Contains("task SM-01 is named 'Request a new supplier'; a task's name starts with its step identifier", StringComparison.Ordinal));
    }

    [Fact]
    [Proves("ADR-0045")]
    public void APackFindsTheMapsOfTheFolderNamedAfterIt()
    {
        var packs = Directory.CreateDirectory(Path.Combine(_folder.FullName, "suites", "probe", ProcessMapCheck.PackFolder));
        File.WriteAllText(Path.Combine(packs.FullName, "orders.md"), "# Pack");
        File.WriteAllText(Path.Combine(packs.FullName, "receiving.md"), "# Pack");
        Directory.CreateDirectory(Path.Combine(packs.FullName, "orders"));
        File.WriteAllText(Path.Combine(packs.FullName, "orders", "main-flow.bpmn"), Sound);

        var found = ProcessMapCheck.FindPacks(_folder.FullName);

        Assert.Equal(["orders.md", "receiving.md"], found.Select(p => Path.GetFileName(p.Pack)));
        Assert.Equal(["main-flow.bpmn"], found[0].Maps.Select(Path.GetFileName));
        Assert.Empty(found[1].Maps);
    }

    [Fact]
    [Proves("ADR-0045")]
    public void ThePacksMapsInTheRepositoryPass()
    {
        var root = Path.Combine(RepositoryRoot(), "docs", "prd");
        var packs = ProcessMapCheck.FindPacks(root);

        Assert.NotEmpty(packs);
        Assert.All(packs, pack => Assert.NotEmpty(pack.Maps));
        Assert.Empty(packs.SelectMany(pack => pack.Maps).SelectMany(map => ProcessMapCheck.Check(map, Schemas)));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"Foundation.slnx was not found above {AppContext.BaseDirectory}.");
    }
}
