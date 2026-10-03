using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Eslee.TrayFolder.Services;
using Eslee.TrayIntegration;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const string ClientId = "audit.client";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "host") return RunHost(args[1], args[2]);
            if (args.Length > 0 && args[0] == "secondary")
            {
                using var instance = new SingleInstanceManager(args[1]);
                Check(instance.Role == SingleInstanceRole.Secondary, "secondary role");
                instance.SignalPrimary();
                Console.WriteLine("SECONDARY");
                return 0;
            }
            RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int RunHost(string name, string root)
    {
        using var config = new ConfigService(new AppPaths(root));
        var state = config.LoadOrCreateAsync().GetAwaiter().GetResult();
        state.Config.Apps[0].ExecutablePath = "audit-only.exe";
        config.SaveAsync(state.Config).GetAwaiter().GetResult();
        Check(config.LoadOrCreateAsync().GetAwaiter().GetResult().Config.Apps[0].ExecutablePath == "audit-only.exe", "config roundtrip");
        using var instance = new SingleInstanceManager(name);
        Check(instance.Role == SingleInstanceRole.Primary, "primary role");
        instance.Listen(() => Console.WriteLine("ACTIVATED"));
        using var server = new TrayHostServer(name);
        server.ClientRegistered += (_, _) => Console.WriteLine("REGISTERED");
        server.ClientDisconnected += (_, _) => Console.WriteLine("DISCONNECTED");
        server.Start();
        Console.WriteLine("READY");
        while (Console.ReadLine() is { } command && command != "exit")
        {
            if (command != "roundtrip") throw new InvalidOperationException("Unexpected control command.");
            Check(server.SendTrayModeAsync(ClientId, TrayMode.Hosted, CancellationToken.None).GetAwaiter().GetResult(), "hosted mode sent");
            var menu = server.GetMenuAsync(ClientId, Timeout, CancellationToken.None).GetAwaiter().GetResult();
            Check(menu?.Single().Id == "audit.action", "menu response");
            Check(server.SendCommandAsync(ClientId, TrayHostCommand.Activate, Timeout, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "command response");
            Console.WriteLine("ROUNDTRIP");
        }
        return 0;
    }

    private static Process StartChild(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Child did not start.");
    }

    private static async Task RunAsync()
    {
        var name = "eslee.audit.os-smoke." + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(root);
        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                using var host = StartChild("host", name, root);
                try
                {
                    await ExpectAsync(host, "READY");
                    using (var secondary = StartChild("secondary", name))
                    {
                        Check(await secondary.StandardOutput.ReadLineAsync().WaitAsync(Timeout) == "SECONDARY", "cross-process secondary");
                        await secondary.WaitForExitAsync().WaitAsync(Timeout);
                        Check(secondary.ExitCode == 0, "secondary exit");
                    }
                    await ExpectAsync(host, "ACTIVATED");
                    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(5000);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    await writer.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage
                    {
                        Type = TrayPipeProtocol.RegisterType, ProtocolVersion = 1, AppId = ClientId,
                        DisplayName = "Isolated OS smoke", ProcessId = Environment.ProcessId, Mode = "standalone",
                    }));
                    await ExpectAsync(host, "REGISTERED");
                    await host.StandardInput.WriteLineAsync("roundtrip");
                    var mode = await ReadAsync(reader);
                    Check(mode.Type == TrayPipeProtocol.SetTrayModeType && mode.Mode == "hosted", "hosted mode received");
                    var menu = await ReadAsync(reader);
                    Check(menu.Type == TrayPipeProtocol.GetMenuType, "menu request");
                    await writer.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage
                    {
                        Type = TrayPipeProtocol.MenuType, Id = menu.Id,
                        Items = [new TrayMenuItemPayload { Id = "audit.action", Text = "Audit action", Enabled = true }],
                    }));
                    var command = await ReadAsync(reader);
                    Check(command.Type == TrayPipeProtocol.CommandType && command.Command == "activate", "activate request");
                    await writer.WriteLineAsync(TrayPipeProtocol.Serialize(new TrayPipeMessage { Type = TrayPipeProtocol.CommandResultType, Id = command.Id, Succeeded = true }));
                    await ExpectAsync(host, "ROUNDTRIP");
                    await host.StandardInput.WriteLineAsync("exit");
                    Check(await reader.ReadLineAsync().WaitAsync(Timeout) is null, "host exit closes client pipe");
                    await host.WaitForExitAsync().WaitAsync(Timeout);
                    Check(host.ExitCode == 0, "graceful host exit: " + await host.StandardError.ReadToEndAsync());
                    Console.WriteLine($"PASS cycle={cycle + 1}: isolated config, process mutex/event, registration, hosted mode, menu, command, host shutdown EOF");
                }
                finally
                {
                    if (!host.HasExited)
                    {
                        await host.StandardInput.WriteLineAsync("exit");
                        try { await host.WaitForExitAsync().WaitAsync(Timeout); }
                        catch (TimeoutException) { host.Kill(true); await host.WaitForExitAsync(); }
                    }
                }
            }
            Console.WriteLine("PASS: same namespace reclaimed after host exit; client reconnected to a new host process.");
        }
        finally
        {
            // Only the random root created above is eligible for cleanup.
            Check(Path.GetFileName(root) == name && Path.GetDirectoryName(root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "cleanup boundary");
            Directory.Delete(root, true);
            Console.WriteLine("CLEANUP: isolated fixture deleted; no user app was launched or terminated.");
        }
    }

    private static async Task<TrayPipeMessage> ReadAsync(StreamReader reader) =>
        TrayPipeProtocol.TryDeserialize(await reader.ReadLineAsync().WaitAsync(Timeout) ?? throw new EndOfStreamException()) ?? throw new IOException("Invalid protocol message.");
    private static async Task ExpectAsync(Process child, string expected) =>
        Check(await child.StandardOutput.ReadLineAsync().WaitAsync(Timeout) == expected, "host event " + expected);
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
