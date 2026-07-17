namespace C3OSS.Codexcw;

internal static class ClaudeArgs
{
    public static PreparedRun Prepare(Request request)
    {
        Validate(request);

        var schema = request.OutputSchema;
        if (!string.IsNullOrEmpty(request.OutputSchemaPath))
        {
            try
            {
                schema = File.ReadAllText(request.OutputSchemaPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidRequestException($"read output schema {request.OutputSchemaPath}: {ex.Message}");
            }
        }

        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        if (!string.IsNullOrEmpty(request.Model))
        {
            args.Add("--model");
            args.Add(request.Model);
        }
        if (request.PermissionMode is { } mode)
        {
            args.Add("--permission-mode");
            args.Add(mode.ToWire());
        }
        foreach (var tool in request.AllowedTools)
        {
            args.Add("--allowed-tools");
            args.Add(tool);
        }
        foreach (var tool in request.DisallowedTools)
        {
            args.Add("--disallowed-tools");
            args.Add(tool);
        }
        if (request.DangerouslyBypassSandbox)
        {
            args.Add("--dangerously-skip-permissions");
        }
        if (!request.Persistent)
        {
            args.Add("--no-session-persistence");
        }
        foreach (var dir in request.AddDirs)
        {
            args.Add("--add-dir");
            args.Add(dir);
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

        // The claude CLI has no --cd flag; the working directory carries the
        // request dir.
        var workingDirectory = string.IsNullOrEmpty(request.Dir) ? null : request.Dir;
        return new PreparedRun(args, workingDirectory, null);
    }

    private static void Validate(Request request)
    {
        if (request.Prompt.Length == 0 && request.Stdin is null)
        {
            throw new PromptRequiredException();
        }
        if (!string.IsNullOrEmpty(request.OutputSchema) && !string.IsNullOrEmpty(request.OutputSchemaPath))
        {
            throw new InvalidRequestException("output schema path and inline schema are mutually exclusive");
        }
        if (!string.IsNullOrEmpty(request.ResumeId) && request.ResumeLast)
        {
            throw new InvalidRequestException("resume id and resume last are mutually exclusive");
        }

        var unsupported = new (bool Set, string Name)[]
        {
            (request.Images.Count > 0, "images"),
            (!string.IsNullOrEmpty(request.Profile), "profile"),
            (request.Sandbox is not null, "sandbox"),
            (request.Approval is not null, "approval"),
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
                throw new InvalidRequestException($"{name} is not supported by the claude agent");
            }
        }
    }
}
