// Capture-layer smoke test (not compiled into Side). Run: copy this file + CaptureSmoke.csproj.txt (renamed to .csproj)
// to a scratch folder, fix the absolute paths, then dotnet run. Creates its own WinForms window and checks UIA/OCR/logic.
using System.Windows.Forms;
using Side.Win.Capture;
using Side.Win.Core;

var fails = 0;
void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) fails++; }

// Pure logic
Check("normalize https elided", BrowserCapture.NormalizeUrl("naver.com/news?x=1#a") == "https://naver.com/news");
Check("normalize http kept", BrowserCapture.NormalizeUrl("http://User:pw@EXAMPLE.com:8080") == "http://example.com:8080/");
Check("normalize rejects search", BrowserCapture.NormalizeUrl("hello world") is null && BrowserCapture.NormalizeUrl("chrome://newtab") is null);
Check("label block ko", FieldLabels.BlockedRule(new FieldMetadata(Title: "카드 번호")) == FieldBlockRule.Label);
Check("label block pw", FieldLabels.BlockedRule(new FieldMetadata(Title: "Password")) == FieldBlockRule.Label);
Check("secure role", FieldLabels.BlockedRule(new FieldMetadata(Role: FieldMetadata.SecureRole)) == FieldBlockRule.SecureTextField);
Check("autocomplete", FieldLabels.BlockedRule(new FieldMetadata(Autocomplete: "cc-number")) == FieldBlockRule.Autocomplete);
Check("label ok", FieldLabels.BlockedRule(new FieldMetadata(Title: "Search")) is null);
Check("chord", Chord.Notation(0x53, Chord.Modifiers.Control | Chord.Modifiers.Shift) == "Ctrl+Shift+S" && Chord.Notation(0x53, Chord.Modifiers.Shift) is null);
Check("utf8 prefix", TextLimits.PrefixUtf8("가나다", 7) == "가나");

// Typed-text diff logic
var events = new List<CaptureEvent>();
var browser = new BrowserCapture();
var stream = new CaptureStream(o => events.Add((CaptureEvent)o), () => false, browser);
stream.Configure(_ => false, true, false);
stream.Activate("notepad.exe", "Notepad");
var hub = new FocusObserver(stream);
hub.RecordValueChange("", "notepad.exe");
hub.RecordValueChange("Hello there. And", "notepad.exe");
hub.RecordValueChange("Hello there. And more", "notepad.exe");
Check("sentence emitted", events.Count == 1 && events[0].Text == "Hello there." && events[0].Kind == "keyboard.text_input");
hub.RecordValueChange("Replaced", "notepad.exe"); // non-prefix -> reset
hub.RecordValueChange("Replaced text", "notepad.exe");
Check("reset on edit", events.Count == 1);
stream.Configure(_ => false, false, false);
hub.RecordValueChange("Replaced text. x", "notepad.exe");
Check("typed text off", events.Count == 1);
stream.Configure(_ => true, true, false);
Check("excluded blocks", !stream.MayObserve("notepad.exe"));
stream.Configure(_ => false, true, false);
Check("hard block side.exe", !stream.MayObserve("side.exe") && stream.IsExcluded("keepassxc.exe"));
Check("event json", System.Text.Json.JsonSerializer.Serialize(events[0], CaptureProtocol.Json).Contains("\"source\":\"mac_ax\""));

// UIA + OCR against our own window (from a worker thread, like the router)
var ready = new ManualResetEventSlim();
Form? form = null;
var ui = new Thread(() =>
{
    form = new Form { Text = "Side smoke window", Width = 700, Height = 400, BackColor = System.Drawing.Color.White };
    form.Controls.Add(new Label { Text = "안녕하세요 라벨 견적 테스트 Hello OCR 12345", AutoSize = true, Top = 20, Left = 20, Font = new System.Drawing.Font("Malgun Gothic", 20) });
    form.Controls.Add(new TextBox { Text = "visible text", Top = 120, Left = 20, Width = 300 });
    form.Controls.Add(new TextBox { Text = "secret", UseSystemPasswordChar = true, Top = 170, Left = 20, Width = 300 });
    form.Shown += (_, _) => ready.Set();
    Application.Run(form);
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ready.Wait(10000);
await Task.Delay(500);
var hwnd = form!.Handle;
await Task.Run(async () =>
{
    var automation = Uia.Client;
    Check("uia client", automation is not null);
    var el = automation!.ElementFromHandle(hwnd);
    Check("uia element name", Uia.Name(el!) == "Side smoke window");
    var text = UiaSnapshot.Extract(el!);
    Console.WriteLine("  snapshot: " + text.Replace("\n", " | "));
    Check("uia snapshot has label", text.Contains("안녕하세요"));
    Check("uia snapshot no secret", !text.Contains("secret"));
    var edit = UiaSnapshot.FindFirst(el!, e => Uia.ControlType(e) == Uia.ControlTypeEdit, skipDocuments: true);
    Check("find edit", edit is not null);
    if (edit is not null) Console.WriteLine($"  edit value={Uia.StringProperty(edit, Uia.PropValueValue)} textField={Uia.IsTextField(edit)} pw={Uia.IsPassword(edit)}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var ocr = await WindowOcr.CaptureTextAsync(hwnd, default);
    Console.WriteLine($"  ocr ({sw.ElapsedMilliseconds}ms): {ocr?.Replace("\n", " | ")}");
    Check("ocr text", ocr is not null && ocr.Contains("안녕하세요"));
    var edits = new List<IUIAutomationElement>();
    var walker = automation.get_ControlViewWalker();
    foreach (var child in UiaSnapshot.Children(walker, el!, 50)) if (Uia.ControlType(child) == Uia.ControlTypeEdit) edits.Add(child);
    Check("two edits", edits.Count == 2);
    Check("password slot", edits.Count(Uia.IsPassword) == 1);
    Check("pid slot", Uia.ProcessId(el!) == Environment.ProcessId);
    Check("hwnd slot", Uia.Try(el!.get_CurrentNativeWindowHandle) == hwnd);
    var plain = edits.First(e => !Uia.IsPassword(e));
    var box = (TextBox)form!.Controls[1];
    form.Invoke(() => { box.Focus(); box.Select(0, 7); });
    await Task.Delay(300);
    Check("focus slot", Uia.HasFocus(plain));
    var focused = automation.GetFocusedElement();
    Check("focused element", focused is not null && Uia.Name(focused) is not null);
    var sel = Uia.SelectedText(plain, 100);
    Console.WriteLine($"  selection=[{sel}]");
    Check("text pattern selection", sel == "visible");
    Check("metadata", Uia.Metadata(edits.First(Uia.IsPassword)).Role == FieldMetadata.SecureRole);
});
form.Invoke(() => form.Close());
Console.WriteLine($"fails={fails}");
