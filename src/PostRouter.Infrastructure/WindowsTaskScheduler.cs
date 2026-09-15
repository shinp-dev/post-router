using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;

namespace PostRouter.Infrastructure;

public sealed record ScheduledWorkerStatus(bool Installed, bool DefinitionValid, string? ExecutionTimeLimit, string? MultipleInstancesPolicy);

public sealed class WindowsTaskScheduler
{
    public const string TaskName = "PostRouter-Worker";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string BuildTaskXml(string executable, string dataDirectory, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var command = Path.GetFullPath(executable);
        var data = Path.GetFullPath(dataDirectory);
        var document = new XDocument(
            new XElement(TaskNamespace + "Task", new XAttribute("version", "1.4"),
                new XElement(TaskNamespace + "RegistrationInfo",
                    new XElement(TaskNamespace + "Description", "Post Router durable publication worker")),
                new XElement(TaskNamespace + "Triggers",
                    new XElement(TaskNamespace + "LogonTrigger",
                        new XElement(TaskNamespace + "Enabled", "true"),
                        new XElement(TaskNamespace + "UserId", userSid))),
                new XElement(TaskNamespace + "Principals",
                    new XElement(TaskNamespace + "Principal", new XAttribute("id", "Author"),
                        new XElement(TaskNamespace + "UserId", userSid),
                        new XElement(TaskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(TaskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskNamespace + "Settings",
                    new XElement(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNamespace + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNamespace + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNamespace + "AllowHardTerminate", "true"),
                    new XElement(TaskNamespace + "StartWhenAvailable", "true"),
                    new XElement(TaskNamespace + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskNamespace + "AllowStartOnDemand", "true"),
                    new XElement(TaskNamespace + "Enabled", "true"),
                    new XElement(TaskNamespace + "Hidden", "false"),
                    new XElement(TaskNamespace + "RunOnlyIfIdle", "false"),
                    new XElement(TaskNamespace + "WakeToRun", "false"),
                    new XElement(TaskNamespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskNamespace + "Priority", "7"),
                    new XElement(TaskNamespace + "RestartOnFailure",
                        new XElement(TaskNamespace + "Interval", "PT1M"),
                        new XElement(TaskNamespace + "Count", "3"))),
                new XElement(TaskNamespace + "Actions", new XAttribute("Context", "Author"),
                    new XElement(TaskNamespace + "Exec",
                        new XElement(TaskNamespace + "Command", command),
                        new XElement(TaskNamespace + "Arguments", $"--data-dir {QuoteWindowsArgument(data)} worker run")))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static async Task InstallAsync(string executable, string dataDirectory, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Task Scheduler is only available on Windows.");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
        var temporary = Path.Combine(Path.GetTempPath(), $"post-router-task-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(temporary, BuildTaskXml(executable, dataDirectory, sid), cancellationToken).ConfigureAwait(false);
            await RunAsync(["/Create", "/F", "/TN", TaskName, "/XML", temporary], cancellationToken).ConfigureAwait(false);
            var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.DefinitionValid) throw new InvalidOperationException("Task Scheduler registration did not preserve required worker settings.");
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public static Task StartAsync(CancellationToken cancellationToken = default) => RunAsync(["/Run", "/TN", TaskName], cancellationToken);
    public static Task StopAsync(CancellationToken cancellationToken = default) => RunAsync(["/End", "/TN", TaskName], cancellationToken);
    public static async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        _ = await RunCaptureAsync(["/End", "/TN", TaskName], allowNotFound: true, cancellationToken).ConfigureAwait(false);
        await RunAsync(["/Delete", "/F", "/TN", TaskName], cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ScheduledWorkerStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        var result = await RunCaptureAsync(["/Query", "/TN", TaskName, "/XML"], allowNotFound: true, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return new(false, false, null, null);
        var document = XDocument.Parse(result.Output, LoadOptions.None);
        var limit = document.Descendants(TaskNamespace + "ExecutionTimeLimit").SingleOrDefault()?.Value;
        var multiple = document.Descendants(TaskNamespace + "MultipleInstancesPolicy").SingleOrDefault()?.Value;
        return new(true, string.Equals(limit, "PT0S", StringComparison.Ordinal) && string.Equals(multiple, "IgnoreNew", StringComparison.Ordinal), limit, multiple);
    }

    private static async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunCaptureAsync(arguments, allowNotFound: false, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Task Scheduler operation failed with exit code {result.ExitCode}.");
    }

    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(IReadOnlyList<string> arguments, bool allowNotFound, CancellationToken cancellationToken)
    {
        EnsureWindows();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start schtasks.exe.");
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && !allowNotFound)
            throw new InvalidOperationException($"Task Scheduler operation failed with exit code {process.ExitCode}.");
        return (process.ExitCode, output);
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Contains('"')) throw new ArgumentException("Windows task paths cannot contain a quote.", nameof(value));
        var trailingBackslashes = value.Reverse().TakeWhile(character => character == '\\').Count();
        return '"' + value + new string('\\', trailingBackslashes) + '"';
    }
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Task Scheduler is only available on Windows.");
    }
}
