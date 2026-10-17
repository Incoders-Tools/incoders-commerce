using System.Text;

namespace Commerce.Cloud.Api.Operations;

/// <summary>
/// Operator commands run inside the Cloud.Api executable instead of the web host
/// (railway-deploy-pipeline S12): `dotnet Commerce.Cloud.Api.dll &lt;command&gt; ...` from the Railway service Console or
/// `railway ssh`. Program.cs asks here first; when the first argument names no command the web host starts exactly as
/// before. No command has an HTTP surface.
/// </summary>
public static class OperationsCommands
{
    /// <summary>
    /// Runs the command named by <paramref name="args"/>[0] and returns its exit code, or null when the first argument
    /// is not a command (including no arguments at all), so the caller starts the web host.
    /// </summary>
    public static async Task<int?> TryRunAsync(string[] args, IConfiguration configuration, CommandConsole console, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return null;
        }

        return args[0] switch
        {
            CreateSystemAdminCommand.Name => await CreateSystemAdminCommand.RunAsync(args[1..], configuration, console, ct),
            _ => null,
        };
    }
}

/// <summary>
/// The terminal a command talks to. <see cref="System"/> is the real process console; tests pass redirected readers
/// and writers. A secret is read without echo from an interactive terminal, or as one line from redirected stdin
/// (scripted recovery, tests) — never from an argument or an environment variable.
/// </summary>
public sealed class CommandConsole(TextReader input, bool inputRedirected, TextWriter output, TextWriter error)
{
    public static CommandConsole System => new(Console.In, Console.IsInputRedirected, Console.Out, Console.Error);

    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public string ReadSecret(string prompt)
    {
        if (inputRedirected)
        {
            return input.ReadLine() ?? string.Empty;
        }

        Output.Write(prompt);
        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }

        Output.WriteLine();
        return secret.ToString();
    }
}
