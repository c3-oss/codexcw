using System.Text;

namespace C3OSS.Codexcw;

internal sealed record PreparedRun(
    IReadOnlyList<string> Args,
    string? WorkingDirectory,
    string? SchemaTempPath);

internal static class CodexArgs
{
    public static PreparedRun Prepare(Request request, SandboxMode defaultSandbox, ApprovalPolicy defaultApproval)
    {
        Validate(request);

        var schemaPath = request.OutputSchemaPath;
        string? schemaTempPath = null;
        if (!string.IsNullOrEmpty(request.OutputSchema))
        {
            schemaTempPath = Path.Combine(
                Path.GetTempPath(),
                $"codexcw-schema-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(schemaTempPath, request.OutputSchema);
            }
            catch
            {
                File.Delete(schemaTempPath);
                throw;
            }
            schemaPath = schemaTempPath;
        }

        var resume = !string.IsNullOrEmpty(request.ResumeId) || request.ResumeLast;
        var args = new List<string> { "exec" };
        if (resume)
        {
            args.Add("resume");
        }

        AppendCommonArgs(args, request, schemaPath, resume, defaultSandbox, defaultApproval);

        if (request.ResumeLast)
        {
            args.Add("--last");
        }
        if (request.ResumeAll)
        {
            args.Add("--all");
        }
        if (!string.IsNullOrEmpty(request.ResumeId))
        {
            args.Add(request.ResumeId);
        }
        args.Add("-");

        return new PreparedRun(args, null, schemaTempPath);
    }

    private static void Validate(Request request)
    {
        if (request.Prompt.Length == 0 && request.Stdin is null)
        {
            throw new PromptRequiredException();
        }
        if (request.PermissionMode is not null ||
            request.AllowedTools.Count > 0 ||
            request.DisallowedTools.Count > 0)
        {
            throw new InvalidRequestException("permission mode and tool filters require the claude agent");
        }
        if (!string.IsNullOrEmpty(request.OutputSchema) && !string.IsNullOrEmpty(request.OutputSchemaPath))
        {
            throw new InvalidRequestException("output schema path and inline schema are mutually exclusive");
        }
        var resume = !string.IsNullOrEmpty(request.ResumeId) || request.ResumeLast;
        if (!string.IsNullOrEmpty(request.ResumeId) && request.ResumeLast)
        {
            throw new InvalidRequestException("resume id and resume last are mutually exclusive");
        }
        if (request.ResumeAll && !resume)
        {
            throw new InvalidRequestException("resume all requires resume id or resume last");
        }
        if (resume &&
            (!string.IsNullOrEmpty(request.Dir) || request.AddDirs.Count > 0 || !string.IsNullOrEmpty(request.Profile)))
        {
            throw new InvalidRequestException("dir, add dirs, and profile are not supported by codex exec resume");
        }
    }

    private static void AppendCommonArgs(
        List<string> args,
        Request request,
        string? schemaPath,
        bool resume,
        SandboxMode defaultSandbox,
        ApprovalPolicy defaultApproval)
    {
        args.Add("--json");
        if (!resume)
        {
            args.Add("--color");
            args.Add("never");
        }
        if (request.StrictConfig)
        {
            args.Add("--strict-config");
        }
        if (!string.IsNullOrEmpty(request.Model))
        {
            args.Add("-m");
            args.Add(request.Model);
        }
        if (!string.IsNullOrEmpty(request.Profile) && !resume)
        {
            args.Add("-p");
            args.Add(request.Profile);
        }
        foreach (var feature in request.Enable)
        {
            args.Add("--enable");
            args.Add(feature);
        }
        foreach (var feature in request.Disable)
        {
            args.Add("--disable");
            args.Add(feature);
        }
        foreach (var image in request.Images)
        {
            args.Add("-i");
            args.Add(image);
        }
        if (!request.RequireGitRepo)
        {
            args.Add("--skip-git-repo-check");
        }
        if (!request.Persistent)
        {
            args.Add("--ephemeral");
        }
        if (request.IgnoreUserConfig)
        {
            args.Add("--ignore-user-config");
        }
        if (request.IgnoreRules)
        {
            args.Add("--ignore-rules");
        }
        if (request.DangerouslyBypassSandbox)
        {
            args.Add("--dangerously-bypass-approvals-and-sandbox");
        }
        else
        {
            var sandbox = (request.Sandbox ?? defaultSandbox).ToWire();
            if (resume)
            {
                args.Add("-c");
                args.Add($"sandbox_mode=\"{sandbox}\"");
            }
            else
            {
                args.Add("--sandbox");
                args.Add(sandbox);
            }
            var approval = (request.Approval ?? defaultApproval).ToWire();
            args.Add("-c");
            args.Add($"approval_policy=\"{approval}\"");
        }
        if (request.DangerouslyBypassHooks)
        {
            args.Add("--dangerously-bypass-hook-trust");
        }
        if (!resume)
        {
            if (!string.IsNullOrEmpty(request.Dir))
            {
                args.Add("-C");
                args.Add(request.Dir);
            }
            foreach (var dir in request.AddDirs)
            {
                args.Add("--add-dir");
                args.Add(dir);
            }
        }
        if (!string.IsNullOrEmpty(schemaPath))
        {
            args.Add("--output-schema");
            args.Add(schemaPath);
        }
        if (!string.IsNullOrEmpty(request.OutputLastMessagePath))
        {
            args.Add("-o");
            args.Add(request.OutputLastMessagePath);
        }
        foreach (var @override in request.Config)
        {
            args.Add("-c");
            args.Add(@override.ToString());
        }
    }

    /// <summary>
    /// Writes the prompt payload to the agent's stdin: the prompt, the stdin
    /// stream, or the prompt with the stream wrapped in stdin markers.
    /// </summary>
    public static async Task WritePromptAsync(Request request, Stream stdin, CancellationToken cancellationToken)
    {
        if (request.Prompt.Length > 0)
        {
            var prompt = Encoding.UTF8.GetBytes(request.Prompt);
            await stdin.WriteAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        if (request.Stdin is not null)
        {
            if (request.Prompt.Length > 0)
            {
                var open = Encoding.UTF8.GetBytes("\n\n<stdin>\n");
                await stdin.WriteAsync(open, cancellationToken).ConfigureAwait(false);
            }
            await request.Stdin.CopyToAsync(stdin, cancellationToken).ConfigureAwait(false);
            if (request.Prompt.Length > 0)
            {
                var close = Encoding.UTF8.GetBytes("\n</stdin>\n");
                await stdin.WriteAsync(close, cancellationToken).ConfigureAwait(false);
            }
        }
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
