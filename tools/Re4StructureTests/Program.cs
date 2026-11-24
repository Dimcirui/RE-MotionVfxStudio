// Structural edits the preview window sends (through PreviewEditorSession, without the window):
// add_attribute with RE4 default fields, add_entry from a Type template, duplicate, copy/paste and remove
// entries, and the preview target following its entry while positions shift.
// Usage: dotnet run -c Release --project tools/Re4StructureTests
using System.Text.Json.Nodes;
using VfxStudio.Re4Render;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string game = Path.GetDirectoryName(exe)!;
string repo = FindRepository();
int checks = 0, failures = 0;
void Check(bool ok, string name, object? detail = null)
{
    checks++;
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (detail == null ? "" : " :: " + detail));
    if (!ok) failures++;
}
var catalog = PreviewAttributeCatalog.Re4;
JsonArray Fields(string type) => new(catalog.Defaults(type).Select(f => (JsonNode?)new JsonObject { ["path"] = f.Key, ["value"] = f.Value }).ToArray());
JsonObject Entry(int index, int? attribute = null)
{
    var t = new JsonObject { ["kind"] = "entry", ["index"] = index };
    if (attribute is int a) t["attributeIndex"] = a;
    return t;
}
int Entries(JsonObject reply) => reply["document"]!["entries"]!.AsArray().Count;

string root = Path.Combine(repo, "test-output", "re4-structure-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
var fixture = PreviewDocument.Fixture(game, "bb2");
using var editor = new PreviewEditorSession(repo, root, protectedRoots: [game]);
var state = await editor.OpenAsync(fixture.Path, PreviewDocument.ReadEntryId(fixture.Path, fixture.Offset));
int current = state.Manifest!.EntryPosition;
Check(current == 13, "bb2 opens at entry 13", current);

// 1. add_attribute with the catalogue defaults: one revision, the fields read back as given.
var (added, reply) = await editor.SendRawAsync("add_attribute", new() { ["target"] = Entry(current), ["type"] = "Blink", ["fields"] = Fields("Blink") }, state.Revision);
int blink = added.Attributes!.Single(a => a.Type == "Blink").Index;
var inspected = await editor.InspectAsync(added.Manifest!.DocumentId, added.Revision, blink);
var values = inspected.Inspection!.Fields.ToDictionary(f => f.Path, f => f.Value);
var wrong = catalog.Defaults("Blink").Where(d => values.GetValueOrDefault(d.Key) != d.Value).Select(d => $"{d.Key}={values.GetValueOrDefault(d.Key)} (want {d.Value})").ToArray();
Check(added.Revision == state.Revision + 1 && catalog.Defaults("Blink").Count > 0 && wrong.Length == 0, "add_attribute writes the RE4 default fields in the same revision",
    $"{catalog.Defaults("Blink").Count} fields; " + string.Join(", ", wrong.Take(3)));
state = added;

// 2. An initial field that does not exist is refused and nothing changes.
try
{
    await editor.SendRawAsync("add_attribute", new() { ["target"] = Entry(current), ["type"] = "RotateAnim", ["fields"] = new JsonArray(new JsonObject { ["path"] = "NoSuchField", ["value"] = "1" }) }, state.Revision);
    Check(false, "add_attribute refuses an unknown initial field");
}
catch (PreviewBackendException ex)
{
    Check(editor.State!.Revision == state.Revision && editor.State.Attributes!.All(a => a.Type != "RotateAnim"), "add_attribute refuses an unknown initial field", ex.Message);
}

// 3. add_entry from the Billboard3D template: one revision, appended, selectable, and it emits particles.
var template = catalog.Template("TypeBillboard3D");
var attributes = new JsonArray(template.Select(t => (JsonNode?)new JsonObject { ["type"] = t, ["fields"] = Fields(t) }).ToArray());
(state, reply) = await editor.SendRawAsync("add_entry", new() { ["name"] = "New_Billboard3D", ["attributes"] = attributes }, state.Revision);
int count = Entries(reply), created = reply["selection"]!["index"]!.GetValue<int>();
Check(created == count - 1 && state.Manifest!.EntryPosition == current && state.PublicationError == null, "add_entry appends one entry and keeps the preview target",
    $"template {string.Join(",", template)}; entries {count}");
state = await editor.SelectEntryAsync(created);
Check(state.Manifest!.EntryPosition == created && state.Attributes!.Select(a => a.Type).Order().SequenceEqual(template.Order()), "the new entry is selectable with the template attributes",
    string.Join(",", state.Attributes!.Select(a => a.Type)));
using (var resources = new Re4ResourceSource(game))
using (var native = new NativePreviewClient("python", Path.Combine(repo, "tools", "Re4Native", "re4host.py"), exe))
{
    var document = PreviewDocument.Read(state.Manifest.Path, state.Manifest.Offset, resources);
    await native.LoadAsync(document.LoadCommand());
    using var frame = await native.FrameAsync(45);
    Check(frame.Particles.Count > 0 && frame.Particles.All(p => float.IsFinite(p.Position.X)), "the new Billboard3D entry emits particles in re4.exe's emitter", $"alive {frame.Particles.Count} at tick 45");
}
current = created;

// 4. Removing an entry before the target: the preview follows the target's ID; undo moves it back.
(state, reply) = await editor.SendRawAsync("remove_entry", new() { ["index"] = 2 }, state.Revision);
Check(state.Manifest!.EntryPosition == current - 1 && state.PublicationError == null && Entries(reply) == count - 1, "removing an earlier entry shifts the preview target with its entry",
    $"target {state.Manifest.EntryPosition}, error {state.PublicationError}");
state = await editor.CommandAsync("undo");
Check(state.Manifest!.EntryPosition == current && state.PublicationError == null, "undo restores the entry and the target position", state.Manifest.EntryPosition);

// 5. Duplicate and copy/paste an entry: both appended with the same attribute types.
var types = state.Attributes!.Select(a => a.Type).ToArray();
(state, reply) = await editor.SendRawAsync("duplicate_entry", new() { ["index"] = current }, state.Revision);
int duplicate = Entries(reply) - 1;
state = await editor.SelectEntryAsync(duplicate);
Check(state.Attributes!.Select(a => a.Type).SequenceEqual(types), "duplicate_entry appends a copy", duplicate);
// Undoing the duplicate removes the edited entry: the preview moves to the nearest previewable one.
state = await editor.CommandAsync("undo");
Check(state.PublicationError == null && state.Manifest!.EntryPosition == duplicate - 1, "undoing the duplicate that is being edited moves the preview to the nearest entry",
    $"target {state.Manifest?.EntryPosition}, error {state.PublicationError}");
(state, reply) = await editor.SendRawAsync("duplicate_entry", new() { ["index"] = current }, state.Revision);
(_, reply) = await editor.SendRawAsync("copy_efx", new() { ["target"] = Entry(13) });
string clip = reply["copyBuffer"]!["id"]!.GetValue<string>();
var original = (await editor.SelectEntryAsync(13)).Attributes!.Select(a => a.Type).ToArray();
(state, reply) = await editor.SendRawAsync("paste_efx", new() { ["target"] = new JsonObject { ["kind"] = "document", ["index"] = 0 }, ["clipboardId"] = clip }, editor.State!.Revision);
int pasted = reply["selection"]!["index"]!.GetValue<int>();
state = await editor.SelectEntryAsync(pasted);
Check(pasted == Entries(reply) - 1 && state.Attributes!.Select(a => a.Type).SequenceEqual(original), "copy_efx + paste_efx copies entry 13 as a new entry", pasted);

// 6. Removing the edited entry after moving to another one (what the window does).
state = await editor.SelectEntryAsync(13);
int before = Entries(reply);
(state, reply) = await editor.SendRawAsync("remove_entry", new() { ["index"] = pasted }, state.Revision);
Check(Entries(reply) == before - 1 && state.Manifest!.EntryPosition == 13 && state.PublicationError == null, "removing another entry leaves the edited one untouched");

// 7. A decal carrier (what "新建脚本载体 entry → EffectDecal" sends): RE4's usual carrier attributes and an EffectDecal
// PtBehavior with RE4's usual parameters; saved and read back, the entry has them, and the prefab path can change.
var decal = catalog.Behaviors.Find(PreviewBehaviorTemplates.Decal)!;
var carrier = new JsonArray(decal.Carrier.Select(t =>
{
    var spec = new JsonObject { ["type"] = t, ["fields"] = Fields(t) };
    if (t == "PtBehavior") spec["behavior"] = decal.Request();
    return (JsonNode?)spec;
}).ToArray());
(state, reply) = await editor.SendRawAsync("add_entry", new() { ["name"] = "New_EffectDecal", ["attributes"] = carrier }, editor.State!.Revision);
int decalEntry = reply["selection"]!["index"]!.GetValue<int>();
state = await editor.SelectEntryAsync(decalEntry);
int behaviorIndex = state.Attributes!.Single(a => a.Type == "PtBehavior").Index;
var behaviorFields = (await editor.InspectAsync(state.Manifest!.DocumentId, state.Revision, behaviorIndex)).Inspection!.Fields;
var prefabField = behaviorFields.Single(f => f.Path.EndsWith(".variable.str") && behaviorFields.Any(n => n.Path == f.Path.Replace(".variable.str", ".behaviorProperty") && n.Value == "DecalPrefab"));
const string burnt = "RE_ENGINE_LIBRARY/VFX_Library/Decal/Burnt_BDC/decal_capcom_burnt_bdc_00.pfb";
(state, _) = await editor.SendRawAsync("set_fields", new()
{
    ["target"] = Entry(decalEntry, behaviorIndex),
    ["fields"] = new JsonArray(new JsonObject { ["path"] = prefabField.Path, ["value"] = burnt }),
}, state.Revision);
(_, reply) = await editor.SendRawAsync("save", new(), null);
string saved = reply["outputPath"]!.GetValue<string>();
var savedFile = new ReeLib.EfxFile(new ReeLib.FileHandler(new MemoryStream(File.ReadAllBytes(saved)), saved) { FileVersion = (int)ReeLib.Efx.EfxVersion.RE4 });
var savedEntry = savedFile.Read() ? savedFile.Entries[decalEntry] : null;
var savedBehavior = savedEntry?.Attributes.OfType<ReeLib.Efx.Structs.Pt.EFXAttributePtBehavior>().SingleOrDefault();
var expected = decal.Parameters.Select(p => p.Name == "DecalPrefab" ? burnt : p.Value).ToArray();
Check(savedEntry != null && savedEntry.Attributes.Select(a => a.type.ToString()).SequenceEqual(decal.Carrier) &&
      savedBehavior is { behaviorString: PreviewBehaviorTemplates.Decal, unkn1: 2 } &&
      savedBehavior.properties.Select(p => p.behaviorProperty).SequenceEqual(decal.Parameters.Select(p => p.Name)) &&
      savedBehavior.properties.Select(p => p.ValueText()).SequenceEqual(expected),
    "a decal carrier entry (NoDraw + EffectDecal) saves with RE4's carrier attributes and parameters; the prefab path changed to Burnt",
    $"{string.Join(",", savedEntry?.Attributes.Select(a => a.type) ?? [])}; {savedBehavior?.properties.Count} parameters; {Path.GetFileName(saved)}");

// 8. One PtBehavior parameter at a time (behavior_parameter), placed by the class's RE4 canonical order as the MHWs EFX
// Editor does: EmissiveIntensity (Float) and OBBScalarAdd (Range, header 2) go to their places, RandomRollRange leaves from
// the middle; a duplicate is refused. Saved and read back: names follow the canonical order, values are as sent, and
// every header index equals its position (as in all 13773 RE4 parameters).
JsonObject ParameterBody(string action, string name, string? value = null)
{
    var body = new JsonObject { ["target"] = Entry(decalEntry, behaviorIndex), ["operation"] = action, ["name"] = name };
    if (action == "add")
    {
        var c = decal.Candidate(name)!;
        body["type"] = c.Type; body["value"] = value ?? c.Value;
        body["order"] = new JsonArray(decal.Candidates!.Select(x => (JsonNode?)x.Name).ToArray());
    }
    return body;
}
var names = decal.Parameters.Select(p => p.Name).ToList();
(state, _) = await editor.SendRawAsync("behavior_parameter", ParameterBody("add", "EmissiveIntensity", "20"), editor.State!.Revision);
names.Insert(decal.InsertIndex(names, "EmissiveIntensity"), "EmissiveIntensity");
(state, _) = await editor.SendRawAsync("behavior_parameter", ParameterBody("add", "OBBScalarAdd"), state.Revision);
names.Insert(decal.InsertIndex(names, "OBBScalarAdd"), "OBBScalarAdd");
(state, _) = await editor.SendRawAsync("behavior_parameter", ParameterBody("remove", "RandomRollRange"), state.Revision);
names.Remove("RandomRollRange");
try
{
    await editor.SendRawAsync("behavior_parameter", ParameterBody("add", "EmissiveIntensity"), state.Revision);
    Check(false, "behavior_parameter refuses a parameter that is already there");
}
catch (Exception ex) when (ex is PreviewBackendException or InvalidOperationException)
{
    Check(editor.State!.Revision == state.Revision, "behavior_parameter refuses a parameter that is already there", ex.Message);
}
(_, reply) = await editor.SendRawAsync("save", new(), null);
saved = reply["outputPath"]!.GetValue<string>();
savedFile = new ReeLib.EfxFile(new ReeLib.FileHandler(new MemoryStream(File.ReadAllBytes(saved)), saved) { FileVersion = (int)ReeLib.Efx.EfxVersion.RE4 });
savedBehavior = savedFile.Read() ? savedFile.Entries[decalEntry].Attributes.OfType<ReeLib.Efx.Structs.Pt.EFXAttributePtBehavior>().Single() : null;
var savedNames = savedBehavior?.properties.Select(p => p.behaviorProperty!).ToList() ?? [];
var canonical = decal.Candidates!.Select(c => c.Name).ToList();
Check(savedNames.SequenceEqual(names) && savedNames.Select(n => canonical.IndexOf(n)).Zip(savedNames.Select(n => canonical.IndexOf(n)).Skip(1)).All(z => z.First < z.Second),
    "added parameters sit at their canonical places and the removed one is gone", string.Join(",", savedNames));
var emissive = savedBehavior?.properties.Single(p => p.behaviorProperty == "EmissiveIntensity");
var obb = savedBehavior?.properties.Single(p => p.behaviorProperty == "OBBScalarAdd");
Check(emissive?.ValueText() == "20" && obb?.ValueText() == decal.Candidate("OBBScalarAdd")!.Value && obb?.variable?.unkn == 2 && emissive?.variable?.unkn == 1,
    "values as sent (EmissiveIntensity 20, OBBScalarAdd RE4's usual value) with RE4's headers (Range 2, Float 1)", $"{emissive?.ValueText()} / {obb?.ValueText()}");
Check(savedBehavior!.properties.Select((p, i) => p.variable?.re4_unkn1 == i && p.variable?.re4_unkn0 == -1).All(ok => ok),
    "every parameter header index is renumbered to its position");
(state, _) = await editor.SendRawAsync("undo", new(), editor.State!.Revision);
Check(state.CanRedo, "behavior_parameter is one undo step");

Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {checks - failures}/{checks}; session {root}");
if (failures > 0) Environment.ExitCode = 1;

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "RE-MotionVfxStudio.sln"))) return dir.FullName;
    throw new DirectoryNotFoundException("Repository root not found.");
}
