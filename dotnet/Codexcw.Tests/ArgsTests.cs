using System.Text;

namespace C3OSS.Codexcw.Tests;

public sealed class CodexArgsTests
{
    private static PreparedRun Prepare(Request request) =>
        CodexArgs.Prepare(request, SandboxMode.ReadOnly, ApprovalPolicy.Never);

    [Fact]
    public void DefaultsAreAutomationFriendly()
    {
        var prepared = Prepare(new Request { Prompt = "diga oi" });
        var args = prepared.Args;
        Assert.Equal("exec", args[0]);
        Assert.Contains("--json", args);
        Assert.Contains("--color", args);
        Assert.Contains("never", args);
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Contains("--ephemeral", args);
        Assert.Contains("--sandbox", args);
        Assert.Contains("read-only", args);
        Assert.Contains("-c", args);
        Assert.Contains("approval_policy=\"never\"", args);
        Assert.Equal("-", args[^1]);
        Assert.Null(prepared.WorkingDirectory);
        Assert.Null(prepared.SchemaTempPath);
    }

    [Fact]
    public async Task AdvancedArgsAndPromptComposition()
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes("extra"));
        var request = new Request
        {
            Prompt = "prompt",
            Stdin = stdin,
            Dir = "/work",
            AddDirs = ["/other"],
            Images = ["image.png"],
            Model = "gpt-test",
            Profile = "work",
            Sandbox = SandboxMode.WorkspaceWrite,
            Approval = ApprovalPolicy.OnRequest,
            Config = [new ConfigOverride("foo.bar", "\"baz\""), new ConfigOverride("", "raw=true")],
            Enable = ["feature-a"],
            Disable = ["feature-b"],
            StrictConfig = true,
            IgnoreUserConfig = true,
            IgnoreRules = true,
            OutputSchema = """{"type":"object"}""",
            OutputLastMessagePath = "last.txt",
            DangerouslyBypassHooks = true,
            Env = ["IGNORED=1"],
        };

        var prepared = Prepare(request);
        try
        {
            using var payload = new MemoryStream();
            await CodexArgs.WritePromptAsync(request, payload, CancellationToken.None);
            Assert.Equal("prompt\n\n<stdin>\nextra\n</stdin>\n", Encoding.UTF8.GetString(payload.ToArray()));

            Assert.NotNull(prepared.SchemaTempPath);
            var schemaIndex = prepared.Args.ToList().IndexOf("--output-schema");
            Assert.NotEqual(-1, schemaIndex);
            Assert.Equal("""{"type":"object"}""", File.ReadAllText(prepared.Args[schemaIndex + 1]));

            foreach (var want in new[]
            {
                "exec", "--json", "--color", "never", "--strict-config", "-m", "gpt-test",
                "-p", "work", "--enable", "feature-a", "--disable", "feature-b", "-i",
                "image.png", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config",
                "--ignore-rules", "--sandbox", "workspace-write", "-c", "approval_policy=\"on-request\"",
                "--dangerously-bypass-hook-trust", "-C", "/work", "--add-dir", "/other",
                "-o", "last.txt", "foo.bar=\"baz\"", "raw=true", "-",
            })
            {
                Assert.Contains(want, prepared.Args);
            }
        }
        finally
        {
            File.Delete(prepared.SchemaTempPath!);
        }
    }

    [Fact]
    public void ResumeArgsUseConfigOverrides()
    {
        var prepared = Prepare(new Request
        {
            Prompt = "continue",
            ResumeId = "thread-id",
            ResumeAll = true,
            Persistent = true,
            Sandbox = SandboxMode.DangerFullAccess,
            Approval = ApprovalPolicy.Untrusted,
        });
        var args = prepared.Args;

        Assert.Equal("exec", args[0]);
        Assert.Equal("resume", args[1]);
        Assert.Contains("--all", args);
        Assert.Contains("thread-id", args);
        Assert.Contains("sandbox_mode=\"danger-full-access\"", args);
        Assert.Contains("approval_policy=\"untrusted\"", args);
        Assert.DoesNotContain("--ephemeral", args);
        Assert.DoesNotContain("--color", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void BypassSuppressesSandboxAndApproval()
    {
        var args = Prepare(new Request { Prompt = "x", DangerouslyBypassSandbox = true }).Args;
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain("--sandbox", args);
        Assert.DoesNotContain("approval_policy=\"never\"", args);
    }

    [Fact]
    public void ValidationRejectsInvalidRequests()
    {
        Assert.Throws<PromptRequiredException>(() => Prepare(new Request()));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            OutputSchemaPath = "schema.json",
            OutputSchema = "{}",
        }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            ResumeId = "id",
            ResumeLast = true,
        }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request { Prompt = "x", ResumeAll = true }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            ResumeLast = true,
            Dir = ".",
        }));
    }

    [Fact]
    public void ValidationRejectsClaudeOnlyFields()
    {
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request
        {
            Prompt = "x",
            PermissionMode = PermissionMode.AcceptEdits,
        }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request { Prompt = "x", AllowedTools = ["Edit"] }));
        Assert.Throws<InvalidRequestException>(() => Prepare(new Request { Prompt = "x", DisallowedTools = ["Edit"] }));
    }

    [Fact]
    public void ConfigOverrideFormatsLikeCodexExpects()
    {
        Assert.Equal("raw=true", new ConfigOverride("", "raw=true").ToString());
        Assert.Equal("a=b", new ConfigOverride("a", "b").ToString());
    }
}

public sealed class ClaudeArgsTests
{
    [Fact]
    public void AdvancedArgs()
    {
        var prepared = ClaudeArgs.Prepare(new Request
        {
            Prompt = "prompt",
            Dir = "/work",
            AddDirs = ["/other"],
            Model = ClaudeModels.Opus,
            PermissionMode = PermissionMode.AcceptEdits,
            AllowedTools = ["Bash(git *)", "Edit"],
            DisallowedTools = ["WebSearch"],
            OutputSchema = """{"type":"object"}""",
            DangerouslyBypassSandbox = true,
            Persistent = true,
        });

        foreach (var want in new[]
        {
            "-p", "--output-format", "stream-json", "--verbose",
            "--model", "opus", "--permission-mode", "acceptEdits",
            "--allowed-tools", "Bash(git *)", "Edit", "--disallowed-tools", "WebSearch",
            "--dangerously-skip-permissions", "--add-dir", "/other",
            "--json-schema", """{"type":"object"}""",
        })
        {
            Assert.Contains(want, prepared.Args);
        }
        Assert.DoesNotContain("--no-session-persistence", prepared.Args);
        Assert.DoesNotContain("-C", prepared.Args);
        Assert.Equal("/work", prepared.WorkingDirectory);
    }

    [Fact]
    public void ResumeArgs()
    {
        var byId = ClaudeArgs.Prepare(new Request { Prompt = "go on", ResumeId = "sess-9", Persistent = true }).Args;
        var resumeIndex = byId.ToList().IndexOf("--resume");
        Assert.NotEqual(-1, resumeIndex);
        Assert.Equal("sess-9", byId[resumeIndex + 1]);

        var byLast = ClaudeArgs.Prepare(new Request { Prompt = "go on", ResumeLast = true, Persistent = true }).Args;
        Assert.Contains("--continue", byLast);
    }

    [Fact]
    public void SchemaPathContentsAreInlined()
    {
        var schemaPath = Path.Combine(Path.GetTempPath(), $"codexcw-test-schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(schemaPath, """{"type":"object"}""");
        try
        {
            var args = ClaudeArgs.Prepare(new Request { Prompt = "x", OutputSchemaPath = schemaPath }).Args;
            var schemaIndex = args.ToList().IndexOf("--json-schema");
            Assert.NotEqual(-1, schemaIndex);
            Assert.Equal("""{"type":"object"}""", args[schemaIndex + 1]);
        }
        finally
        {
            File.Delete(schemaPath);
        }
    }

    public static TheoryData<string, Request> InvalidRequests => new()
    {
        { "images", new Request { Prompt = "x", Images = ["a.png"] } },
        { "profile", new Request { Prompt = "x", Profile = "work" } },
        { "sandbox", new Request { Prompt = "x", Sandbox = SandboxMode.ReadOnly } },
        { "approval", new Request { Prompt = "x", Approval = ApprovalPolicy.Never } },
        { "config", new Request { Prompt = "x", Config = [new ConfigOverride("a", "b")] } },
        { "enable", new Request { Prompt = "x", Enable = ["f"] } },
        { "disable", new Request { Prompt = "x", Disable = ["f"] } },
        { "strict config", new Request { Prompt = "x", StrictConfig = true } },
        { "ignore user config", new Request { Prompt = "x", IgnoreUserConfig = true } },
        { "ignore rules", new Request { Prompt = "x", IgnoreRules = true } },
        { "require git repo", new Request { Prompt = "x", RequireGitRepo = true } },
        { "output last message path", new Request { Prompt = "x", OutputLastMessagePath = "o.txt" } },
        { "bypass hooks", new Request { Prompt = "x", DangerouslyBypassHooks = true } },
        { "resume all", new Request { Prompt = "x", ResumeId = "id", ResumeAll = true } },
        { "schema conflict", new Request { Prompt = "x", OutputSchemaPath = "schema.json", OutputSchema = "{}" } },
        { "resume id and last", new Request { Prompt = "x", ResumeId = "id", ResumeLast = true } },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void ValidationRejectsCodexOnlyFields(string name, Request request)
    {
        _ = name;
        Assert.Throws<InvalidRequestException>(() => ClaudeArgs.Prepare(request));
    }

    [Fact]
    public void ValidationRequiresPrompt()
    {
        Assert.Throws<PromptRequiredException>(() => ClaudeArgs.Prepare(new Request()));
    }
}
