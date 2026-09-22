using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Moye.Services;

/// <summary>Runs only in the dedicated STA conversion process, never in the notebook UI.</summary>
public static class OfficeConversionWorker
{
    public static int Run(string sourcePath, string outputPdfPath)
    {
        try
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Office conversion requires an STA worker process.");
            sourcePath = Path.GetFullPath(sourcePath); outputPdfPath = Path.GetFullPath(outputPdfPath);
            if (!Path.GetExtension(outputPdfPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The conversion output must be a PDF file.");
            if (File.Exists(outputPdfPath)) throw new IOException("The conversion output already exists.");
            OfficeDocumentValidation.Validate(sourcePath);
            if (Path.GetExtension(sourcePath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
                ConvertWord(sourcePath, outputPdfPath);
            else ConvertPowerPoint(sourcePath, outputPdfPath);
            if (!File.Exists(outputPdfPath) || new FileInfo(outputPdfPath).Length == 0)
                throw new InvalidDataException("Office did not produce a PDF. Open the document in Office and export a PDF copy.");
            return 0;
        }
        catch (Exception exception)
        {
            var cause = exception.GetBaseException();
            Console.Error.WriteLine(cause is COMException
                ? "Microsoft Office could not convert this document. Check that it opens normally and does not require a password, repair or sign-in.\n" + cause.Message
                : cause.Message);
            return 1;
        }
    }

    private static void ConvertWord(string source, string output)
    {
        object? application = null, documents = null, document = null, documentWindow = null, options = null;
        var previousProcesses = SnapshotProcesses("WINWORD");
        bool owned = false, configured = false;
        int alerts = 0, automationSecurity = 0;
        bool linksAtOpen = false, linksAtPrint = false, fieldsAtPrint = false;
        try
        {
            application = CreateApplication("Word.Application", "Microsoft Word");
            dynamic word = application;
            documents = word.Documents;
            // Word is a SingleUse COM server: CreateInstance starts a separate
            // process. Unlike PowerPoint, its Application has no HWND. Check
            // the new process and empty, hidden state before configuring it;
            // confirm the document window's process once a document exists.
            var processId = FindNewProcess("WINWORD", previousProcesses);
            owned = processId is not null && !(bool)word.Visible && (int)((dynamic)documents).Count == 0;
            if (!owned)
                throw SharedApplication("Microsoft Word");
            options = word.Options;
            dynamic settings = options;
            alerts = (int)word.DisplayAlerts; automationSecurity = (int)word.AutomationSecurity;
            linksAtOpen = (bool)settings.UpdateLinksAtOpen;
            linksAtPrint = (bool)settings.UpdateLinksAtPrint;
            fieldsAtPrint = (bool)settings.UpdateFieldsAtPrint;
            configured = true;
            word.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
            word.DisplayAlerts = 0; // wdAlertsNone
            settings.UpdateLinksAtOpen = false;
            settings.UpdateLinksAtPrint = false;
            settings.UpdateFieldsAtPrint = false;
            // Supplying an intentionally unavailable password prevents an
            // unexpected password dialog; encrypted packages are rejected above.
            var unavailablePassword = Guid.NewGuid().ToString("N");
            document = ((dynamic)documents).Open(FileName: source, ConfirmConversions: false,
                ReadOnly: true, AddToRecentFiles: false, PasswordDocument: unavailablePassword,
                PasswordTemplate: unavailablePassword, Revert: false, WritePasswordDocument: unavailablePassword,
                WritePasswordTemplate: unavailablePassword, Visible: false, OpenAndRepair: false, NoEncodingDialog: true);
            documentWindow = ((dynamic)document).ActiveWindow;
            GetWindowThreadProcessId(new IntPtr((int)((dynamic)documentWindow).Hwnd), out var documentProcessId);
            if (documentProcessId != processId)
            {
                owned = false;
                throw SharedApplication("Microsoft Word");
            }
            ((dynamic)document).ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17,
                OpenAfterExport: false, OptimizeFor: 0, Range: 0, Item: 0, IncludeDocProps: false,
                KeepIRM: true, CreateBookmarks: 0, DocStructureTags: true, BitmapMissingFonts: true, UseISO19005_1: false);
        }
        finally
        {
            Release(documentWindow);
            if (document is not null) Try(() => ((dynamic)document).Close(SaveChanges: 0));
            Release(document);
            if (configured && application is not null && options is not null)
            {
                Try(() => ((dynamic)options).UpdateLinksAtOpen = linksAtOpen);
                Try(() => ((dynamic)options).UpdateLinksAtPrint = linksAtPrint);
                Try(() => ((dynamic)options).UpdateFieldsAtPrint = fieldsAtPrint);
                Try(() => ((dynamic)application).DisplayAlerts = alerts);
                Try(() => ((dynamic)application).AutomationSecurity = automationSecurity);
            }
            // A user can open Office while conversion is running. Never quit
            // an instance that became visible or acquired another document.
            if (owned && application is not null && documents is not null)
                Try(() => { if ((int)((dynamic)documents).Count == 0 && !(bool)((dynamic)application).Visible) ((dynamic)application).Quit(SaveChanges: 0); });
            Release(options); Release(documents); Release(application);
        }
    }

    private static void ConvertPowerPoint(string source, string output)
    {
        object? application = null, presentations = null, presentation = null;
        object? slides = null, printOptions = null, printRanges = null, printRange = null;
        var previousProcesses = SnapshotProcesses("POWERPNT");
        // PowerPoint is Multiuse and may return an existing user's instance.
        // Refuse before COM activation, including a running hidden instance.
        if (previousProcesses.Count != 0) throw SharedApplication("Microsoft PowerPoint");
        bool owned = false, configured = false;
        int alerts = 1, automationSecurity = 0;
        try
        {
            application = CreateApplication("PowerPoint.Application", "Microsoft PowerPoint");
            dynamic powerpoint = application;
            presentations = powerpoint.Presentations;
            // A hidden PowerPoint application can throw when reading HWND.
            // Recheck process and COM state in case another launch raced ours.
            owned = FindNewProcess("POWERPNT", previousProcesses) is not null &&
                (int)powerpoint.Visible == 0 && (int)((dynamic)presentations).Count == 0;
            if (!owned)
                throw SharedApplication("Microsoft PowerPoint");
            alerts = (int)powerpoint.DisplayAlerts; automationSecurity = (int)powerpoint.AutomationSecurity;
            configured = true;
            powerpoint.AutomationSecurity = 3;
            powerpoint.DisplayAlerts = 1; // ppAlertsNone
            presentation = ((dynamic)presentations).Open(FileName: source, ReadOnly: -1, Untitled: -1, WithWindow: 0);
            slides = ((dynamic)presentation).Slides;
            var slideCount = (int)((dynamic)slides).Count;
            if (slideCount == 0) throw new InvalidDataException("The presentation has no slides to import.");
            // Some PowerPoint versions omit hidden slides even when the PDF
            // export flag is enabled. Reveal them only in this untitled,
            // read-only conversion copy; never save it back to the source.
            for (var index = 1; index <= slideCount; index++)
            {
                object? slide = null, transition = null;
                try
                {
                    slide = ((dynamic)slides).Item(index);
                    transition = ((dynamic)slide).SlideShowTransition;
                    ((dynamic)transition).Hidden = 0;
                }
                finally { Release(transition); Release(slide); }
            }
            printOptions = ((dynamic)presentation).PrintOptions;
            printRanges = ((dynamic)printOptions).Ranges;
            // The late-bound COM marshaler cannot convert a plain null to the
            // PrintRange interface. Supply the complete range explicitly.
            printRange = ((dynamic)printRanges).Add(1, slideCount);
            ((dynamic)presentation).ExportAsFixedFormat(Path: output, FixedFormatType: 2, Intent: 2,
                FrameSlides: 0, HandoutOrder: 1, OutputType: 1, PrintHiddenSlides: -1, PrintRange: printRange,
                RangeType: 4, SlideShowName: "", IncludeDocProperties: false, KeepIRMSettings: true,
                DocStructureTags: true, BitmapMissingFonts: true, UseISO19005_1: false);
        }
        finally
        {
            Release(printRange); Release(printRanges); Release(printOptions); Release(slides);
            if (presentation is not null)
            {
                Try(() => ((dynamic)presentation).Saved = -1);
                Try(() => ((dynamic)presentation).Close());
            }
            Release(presentation);
            if (configured && application is not null)
            {
                Try(() => ((dynamic)application).DisplayAlerts = alerts);
                Try(() => ((dynamic)application).AutomationSecurity = automationSecurity);
            }
            if (owned && application is not null && presentations is not null)
                Try(() => { if ((int)((dynamic)presentations).Count == 0 && (int)((dynamic)application).Visible == 0) ((dynamic)application).Quit(); });
            Release(presentations); Release(application);
        }
    }

    private static object CreateApplication(string progId, string name)
    {
        var type = Type.GetTypeFromProgID(progId, false) ?? throw new InvalidOperationException(
            $"{name} desktop is not installed. Install it, or export a PDF from your document application first.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Unable to start {name} for conversion.");
    }

    private static HashSet<int> SnapshotProcesses(string name)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(name))
            using (process) ids.Add(process.Id);
        return ids;
    }

    private static int? FindNewProcess(string name, HashSet<int> previousProcesses)
    {
        using var currentProcess = Process.GetCurrentProcess();
        var newProcesses = new List<int>();
        foreach (var process in Process.GetProcessesByName(name))
            using (process)
            {
                try
                {
                    if (!previousProcesses.Contains(process.Id) && process.SessionId == currentProcess.SessionId)
                        newProcesses.Add(process.Id);
                }
                catch (InvalidOperationException) { /* A process exited during the snapshot. */ }
            }
        return newProcesses.Count == 1 ? newProcesses[0] : null;
    }

    private static InvalidOperationException SharedApplication(string name) => new(
        $"{name} is already in use. Moye left that session untouched. Close {name} and retry, or export a PDF there and import the PDF.");

    private static void Try(Action action) { try { action(); } catch { /* Never replace a conversion error during cleanup. */ } }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Try(() => Marshal.FinalReleaseComObject(value)); }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}
