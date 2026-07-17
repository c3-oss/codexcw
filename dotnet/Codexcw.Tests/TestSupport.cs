using System.Runtime.InteropServices;

namespace C3OSS.Codexcw.Tests;

/// <summary>A fact skipped on Windows: the fake agents are POSIX shell scripts.</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "fixture is a POSIX shell script";
        }
    }
}

/// <summary>A fact gated behind an environment variable (live agent tests).</summary>
public sealed class GatedFactAttribute : FactAttribute
{
    public GatedFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
        {
            Skip = $"set {variable}=1 to run against the real agent CLI";
        }
    }
}

/// <summary>Temp-dir scoped test helpers mirroring the Go suite's writeFakeCodex.</summary>
public sealed class FakeAgentDir : IDisposable
{
    private const string Preamble = """
        #!/bin/sh
        set -eu
        record_args() {
          if [ "${CODEXCW_ARGS_FILE:-}" != "" ]; then
            : > "$CODEXCW_ARGS_FILE"
            for arg in "$@"; do
              printf '%s\n' "$arg" >> "$CODEXCW_ARGS_FILE"
            done
          fi
        }

        """;

    public FakeAgentDir()
    {
        Root = Directory.CreateTempSubdirectory("codexcw-test-").FullName;
        ArgsFile = Path.Combine(Root, "args.txt");
        StdinFile = Path.Combine(Root, "stdin.txt");
    }

    public string Root { get; }

    public string ArgsFile { get; }

    public string StdinFile { get; }

    public string WriteFakeCodex(string body)
    {
        var path = Path.Combine(Root, "codex");
        File.WriteAllText(path, Preamble + body.TrimStart('\n'));
#pragma warning disable CA1416 // Unix-only tests.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        return path;
    }

    public Runner NewRunner(string executable, Agent agent = Agent.Codex, int eventBuffer = 0, int stderrLimit = 0) =>
        new(new RunnerOptions
        {
            Agent = agent,
            Executable = executable,
            EventBuffer = eventBuffer,
            StderrLimit = stderrLimit,
            Env = [$"CODEXCW_ARGS_FILE={ArgsFile}", $"CODEXCW_STDIN_FILE={StdinFile}"],
        });

    public string[] ReadArgs() =>
        File.ReadAllText(ArgsFile).Trim().Split('\n');

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Fixtures
{
    public static string Path(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);
#pragma warning disable CA1416 // Unix-only tests.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        return path;
    }
}
