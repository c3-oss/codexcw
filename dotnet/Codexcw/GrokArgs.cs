namespace C3OSS.Codexcw;

internal static class GrokArgs
{
    public static PreparedRun Prepare(
        Request request,
        SandboxMode defaultSandbox,
        ApprovalPolicy defaultApproval)
    {
        Validate(request);

        using var prompt = new MemoryStream();
        CodexArgs.WritePromptAsync(request, prompt, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var promptPath = Path.Combine(
            Path.GetTempPath(),
            $"codexcw-grok-prompt-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllBytes(promptPath, prompt.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteTemp(promptPath);
            throw new ProcessException($"write Grok prompt temp file: {ex.Message}", ex);
        }

        string? schema;
        try
        {
            schema = !string.IsNullOrEmpty(request.OutputSchemaPath)
                ? File.ReadAllText(request.OutputSchemaPath)
                : request.OutputSchema;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteTemp(promptPath);
            throw new InvalidRequestException($"read output schema {request.OutputSchemaPath}: {ex.Message}");
        }

        var args = new List<string>
        {
            "--no-auto-update",
            "--output-format", "streaming-messages-json",
            "--verbatim",
            "--prompt-file", promptPath,
        };
        if (!string.IsNullOrEmpty(request.Dir))
        {
            args.Add("--cwd");
            args.Add(request.Dir);
        }
        if (!string.IsNullOrEmpty(request.Model))
        {
            args.Add("--model");
            args.Add(request.Model);
        }
        if (!string.IsNullOrEmpty(request.Profile))
        {
            args.Add("--agent");
            args.Add(request.Profile);
        }

        var resume = !string.IsNullOrEmpty(request.ResumeId) || request.ResumeLast;
        if (request.DangerouslyBypassSandbox)
        {
            args.Add("--sandbox");
            args.Add("off");
            args.Add("--permission-mode");
            args.Add("bypassPermissions");
        }
        else
        {
            if (!resume || request.Sandbox is not null)
            {
                args.Add("--sandbox");
                args.Add(GrokSandbox(request.Sandbox ?? defaultSandbox));
            }
            args.Add("--permission-mode");
            args.Add(request.PermissionMode is { } mode
                ? mode.ToWire()
                : GrokApproval(request.Approval ?? defaultApproval));
        }

        foreach (var tool in request.AllowedTools)
        {
            args.Add("--allow");
            args.Add(tool);
        }
        foreach (var tool in request.DisallowedTools)
        {
            args.Add("--deny");
            args.Add(tool);
        }
        if (!string.IsNullOrEmpty(schema))
        {
            args.Add("--json-schema");
            args.Add(schema);
        }
        if (!string.IsNullOrEmpty(request.ResumeId))
        {
            args.Add("--resume");
            args.Add(request.ResumeId);
        }
        if (request.ResumeLast)
        {
            args.Add("--continue");
        }

        return new PreparedRun(args, null, null, promptPath, WritePrompt: false);
    }

    private static void Validate(Request request)
    {
        if (request.Prompt.Length == 0 && request.Stdin is null)
        {
            throw new PromptRequiredException();
        }
        if (!string.IsNullOrEmpty(request.OutputSchema) &&
            !string.IsNullOrEmpty(request.OutputSchemaPath))
        {
            throw new InvalidRequestException(
                "output schema path and inline schema are mutually exclusive");
        }
        if (!string.IsNullOrEmpty(request.ResumeId) && request.ResumeLast)
        {
            throw new InvalidRequestException(
                "resume id and resume last are mutually exclusive");
        }
        if (request.Approval is not null && request.PermissionMode is not null)
        {
            throw new InvalidRequestException(
                "approval and permission mode are mutually exclusive for the grok agent");
        }

        var unsupported = new (bool Set, string Name)[]
        {
            (request.AddDirs.Count > 0, "add dirs"),
            (request.Images.Count > 0, "images"),
            (request.Config.Count > 0, "config overrides"),
            (request.Enable.Count > 0, "enable flags"),
            (request.Disable.Count > 0, "disable flags"),
            (request.StrictConfig, "strict config"),
            (request.IgnoreUserConfig, "ignore user config"),
            (request.IgnoreRules, "ignore rules"),
            (request.RequireGitRepo, "require git repo"),
            (!string.IsNullOrEmpty(request.OutputLastMessagePath), "output last message path"),
            (request.DangerouslyBypassHooks, "dangerously bypass hooks"),
            (request.ResumeAll, "resume all"),
        };
        foreach (var (set, name) in unsupported)
        {
            if (set)
            {
                throw new InvalidRequestException(
                    $"{name} is not supported by the grok agent");
            }
        }
    }

    private static string GrokSandbox(SandboxMode mode) => mode switch
    {
        SandboxMode.ReadOnly => "read-only",
        SandboxMode.WorkspaceWrite => "workspace",
        SandboxMode.DangerFullAccess => "off",
        _ => throw new InvalidRequestException($"unknown sandbox mode {(int)mode}"),
    };

    private static string GrokApproval(ApprovalPolicy policy) => policy switch
    {
        ApprovalPolicy.Never => "dontAsk",
        ApprovalPolicy.Untrusted or ApprovalPolicy.OnRequest => "default",
        _ => throw new InvalidRequestException($"unknown approval policy {(int)policy}"),
    };

    private static void DeleteTemp(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
