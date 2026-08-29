using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEditor.Compilation;

using UnityEngine;


[InitializeOnLoad]
public static class UnityPersistentScriptServer
{
    private const string Prefix =
        "http://127.0.0.1:47826/";

    private const string RequiredHeader =
        "AI-Assistant-Local";

    private const int MaxSourceChars =
        50000;

    private const string JobIdKey =
        "AI.PersistentScript.JobId";

    private const string JobStateKey =
        "AI.PersistentScript.State";

    private const string JobAssetPathKey =
        "AI.PersistentScript.AssetPath";

    private const string JobClassNameKey =
        "AI.PersistentScript.ClassName";

    private const string JobDiagnosticsKey =
        "AI.PersistentScript.Diagnostics";

    private const string JobCompileAttemptsKey =
        "AI.PersistentScript.CompileAttempts";

    private const string JobLastCompileRequestTicksKey =
        "AI.PersistentScript.LastCompileRequestTicks";

    private const string JobCompilationScheduledKey =
        "AI.PersistentScript.CompilationScheduled";

    private const string JobCompilationStartedKey =
        "AI.PersistentScript.CompilationStarted";

    private const int MaxCompileStartAttempts =
        2;

    private const double CompileRetryDelaySeconds =
        4.0;

    private const double CompileStartTimeoutSeconds =
        12.0;

    private const double ListenerRetryDelaySeconds =
        1.0;

    private static readonly string[] BlockedSourceFragments =
    {
        "UnityEditor",
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "Microsoft.Win32",
        "DllImport",
        "Assembly.Load",
        "AppDomain",
        "Environment.",
        "Process.",
        "File.",
        "Directory.",
        "HttpClient",
        "WebClient",
        "Thread",
        "Task.Run",
        "unsafe",
        "stackalloc",
        "InitializeOnLoad",
        "ExecuteInEditMode",
        "ExecuteAlways",
        "AssetDatabase",
        "DestroyImmediate",
        "Application.Quit",
        "BuildPipeline",
        "PlayerSettings",
        "PackageManager"
    };

    private static readonly ConcurrentQueue<PendingRequest>
        pendingRequests =
            new ConcurrentQueue<PendingRequest>();

    private static readonly List<string>
        currentCompilerErrors =
            new List<string>();

    private static HttpListener listener;
    private static Thread listenerThread;
    private static volatile bool running;
    private static double nextListenerStartTime;


    static UnityPersistentScriptServer()
    {
        EditorApplication.update += ProcessPendingRequests;
        EditorApplication.update += EnsureStarted;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;

        CompilationPipeline.compilationStarted +=
            OnCompilationStarted;

        CompilationPipeline.assemblyCompilationFinished +=
            OnAssemblyCompilationFinished;

        CompilationPipeline.compilationFinished +=
            OnCompilationFinished;

        EditorApplication.delayCall += RecoverJobAfterReload;
    }


    private static void EnsureStarted()
    {
        if (
            running
            || EditorApplication.timeSinceStartup <
                nextListenerStartTime
        )
        {
            return;
        }

        Start();
    }


    private static void Start()
    {
        if (running)
        {
            return;
        }

        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            listener.Start();

            running = true;

            listenerThread =
                new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "AI Unity Persistent Script Server"
                };

            listenerThread.Start();

            Debug.Log(
                "[AI Persistent Script] Listening on 127.0.0.1:47826"
            );
        }
        catch (Exception ex)
        {
            running = false;

            try
            {
                listener?.Close();
            }
            catch
            {
            }

            listener = null;
            listenerThread = null;

            nextListenerStartTime =
                EditorApplication.timeSinceStartup
                + ListenerRetryDelaySeconds;

            Debug.LogWarning(
                "[AI Persistent Script] Start failed: "
                + ex.Message
                + " Retrying automatically."
            );
        }
    }


    private static void Stop()
    {
        running = false;

        HttpListener listenerToClose =
            listener;

        listener = null;

        try
        {
            listenerToClose?.Stop();
            listenerToClose?.Close();
        }
        catch
        {
            // Unity is closing or reloading editor assemblies.
        }

        listenerThread = null;
    }


    private static void ListenLoop()
    {
        while (running)
        {
            try
            {
                HttpListenerContext context =
                    listener.GetContext();

                QueueHttpRequest(context);
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogError(
                        "[AI Persistent Script] Listener error: "
                        + ex.Message
                    );
                }
            }
        }
    }


    private static void QueueHttpRequest(
        HttpListenerContext context
    )
    {
        try
        {
            if (
                context.Request.Headers["X-AI-Bridge"]
                != RequiredHeader
            )
            {
                WriteResponse(
                    context,
                    403,
                    Failure("authorization", "Unauthorized bridge request.")
                );
                return;
            }

            string path =
                context.Request.Url
                    .AbsolutePath
                    .TrimEnd('/')
                    .ToLowerInvariant();

            if (
                context.Request.HttpMethod == "GET"
                && path == "/script-status"
            )
            {
                string jobId =
                    context.Request.QueryString["jobId"] ?? "";

                pendingRequests.Enqueue(
                    PendingRequest.ForStatus(context, jobId)
                );
                return;
            }

            if (
                context.Request.HttpMethod != "POST"
                || path != "/create-script"
            )
            {
                WriteResponse(
                    context,
                    404,
                    Failure("request", "Unknown persistent-script endpoint.")
                );
                return;
            }

            string body;

            using (
                StreamReader reader =
                    new StreamReader(
                        context.Request.InputStream,
                        context.Request.ContentEncoding
                    )
            )
            {
                body = reader.ReadToEnd();
            }

            CreateScriptRequest request =
                JsonUtility.FromJson<CreateScriptRequest>(body);

            pendingRequests.Enqueue(
                PendingRequest.ForCreate(context, request)
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                context,
                500,
                Failure(
                    "request",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
    }


    private static void ProcessPendingRequests()
    {
        while (
            pendingRequests.TryDequeue(
                out PendingRequest pending
            )
        )
        {
            if (pending.kind == RequestKind.Status)
            {
                HandleStatus(pending);
            }
            else
            {
                HandleCreate(pending);
            }
        }
    }


    private static void HandleCreate(PendingRequest pending)
    {
        CreateScriptRequest request =
            pending.createRequest;

        string validationError =
            ValidateCreateRequest(request);

        if (validationError != null)
        {
            WriteResponse(
                pending.context,
                400,
                Failure("validation", validationError)
            );
            return;
        }

        string normalizedAssetPath =
            NormalizeAssetPath(request.assetPath);

        string projectRoot =
            Directory.GetCurrentDirectory();

        string absolutePath =
            Path.GetFullPath(
                Path.Combine(projectRoot, normalizedAssetPath)
            );

        try
        {
            if (File.Exists(absolutePath))
            {
                string existingSource =
                    File.ReadAllText(absolutePath);

                if (
                    string.Equals(
                        NormalizeNewLines(existingSource),
                        NormalizeNewLines(request.source),
                        StringComparison.Ordinal
                    )
                )
                {
                    string existingJobId =
                        Guid.NewGuid().ToString("N");

                    SetJob(
                        existingJobId,
                        "compiled",
                        normalizedAssetPath,
                        request.className,
                        ""
                    );

                    WriteResponse(
                        pending.context,
                        200,
                        new ScriptResponse
                        {
                            success = true,
                            phase = "create",
                            state = "compiled",
                            jobId = existingJobId,
                            assetPath = normalizedAssetPath,
                            className = request.className,
                            message =
                                "Persistent script already exists with identical source."
                        }
                    );
                    return;
                }

                if (!request.overwrite)
                {
                    WriteResponse(
                        pending.context,
                        409,
                        Failure(
                            "exists",
                            "Script already exists with different source. Set overwrite=true only when the user requested an update."
                        )
                    );
                    return;
                }

                BackupExistingScript(
                    projectRoot,
                    normalizedAssetPath,
                    absolutePath
                );
            }

            string directory =
                Path.GetDirectoryName(absolutePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "Script directory could not be resolved."
                );
            }

            Directory.CreateDirectory(directory);

            File.WriteAllText(
                absolutePath,
                request.source,
                new UTF8Encoding(false)
            );

            string jobId =
                Guid.NewGuid().ToString("N");

            currentCompilerErrors.Clear();

            SetJob(
                jobId,
                "pending",
                normalizedAssetPath,
                request.className,
                ""
            );

            // Close the HTTP response before importing the source.
            // Importing starts compilation and may reload this assembly.
            WriteResponse(
                pending.context,
                200,
                new ScriptResponse
                {
                    success = true,
                    phase = "create",
                    state = "pending",
                    jobId = jobId,
                    assetPath = normalizedAssetPath,
                    className = request.className,
                    message =
                        "Persistent script written. Unity compilation is pending."
                }
            );

            ScheduleCompilation(
                jobId,
                normalizedAssetPath
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                pending.context,
                500,
                Failure(
                    "write",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
    }


    private static void HandleStatus(PendingRequest pending)
    {
        string storedJobId =
            SessionState.GetString(JobIdKey, "");

        if (
            string.IsNullOrWhiteSpace(pending.jobId)
            || !string.Equals(
                pending.jobId,
                storedJobId,
                StringComparison.Ordinal
            )
        )
        {
            WriteResponse(
                pending.context,
                404,
                Failure("status", "Persistent-script job was not found.")
            );
            return;
        }

        string state =
            SessionState.GetString(JobStateKey, "pending");

        string assetPath =
            SessionState.GetString(JobAssetPathKey, "");

        string className =
            SessionState.GetString(JobClassNameKey, "");

        if (
            state == "pending"
        )
        {
            int attempts =
                SessionState.GetInt(
                    JobCompileAttemptsKey,
                    0
                );

            double secondsSinceRequest =
                GetSecondsSinceLastCompileRequest();

            if (
                attempts < MaxCompileStartAttempts
                && (
                    attempts == 0
                    || secondsSinceRequest >= CompileRetryDelaySeconds
                )
            )
            {
                ScheduleCompilation(
                    storedJobId,
                    assetPath
                );
            }
            else if (
                attempts >= MaxCompileStartAttempts
                && secondsSinceRequest >= CompileStartTimeoutSeconds
            )
            {
                SessionState.SetString(
                    JobStateKey,
                    "failed"
                );

                SessionState.SetString(
                    JobDiagnosticsKey,
                    "Unity did not start script compilation after two explicit requests. Check whether the Editor is busy, paused or importing packages."
                );

                state = "failed";
            }
        }

        string diagnostics =
            SessionState.GetString(JobDiagnosticsKey, "");

        WriteResponse(
            pending.context,
            200,
            new ScriptResponse
            {
                success = state != "failed",
                phase = "compile",
                state = state,
                jobId = storedJobId,
                assetPath = assetPath,
                className = className,
                message = state switch
                {
                    "pending" => "Waiting for Unity compilation to start.",
                    "compiling" => "Unity is compiling the persistent script.",
                    "compiled" => "Persistent script compiled successfully.",
                    "failed" => "Persistent script compilation failed.",
                    _ => "Unknown persistent-script state."
                },
                diagnostics = diagnostics
            }
        );
    }


    private static void BeginCompilation(
        string jobId,
        string assetPath
    )
    {
        if (
            string.IsNullOrWhiteSpace(jobId)
            || !string.Equals(
                jobId,
                SessionState.GetString(JobIdKey, ""),
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        try
        {
            int attempts =
                SessionState.GetInt(
                    JobCompileAttemptsKey,
                    0
                );

            if (attempts >= MaxCompileStartAttempts)
            {
                return;
            }

            SessionState.SetInt(
                JobCompileAttemptsKey,
                attempts + 1
            );

            SessionState.SetString(
                JobLastCompileRequestTicksKey,
                DateTime.UtcNow.Ticks.ToString()
            );

            SessionState.SetString(
                JobStateKey,
                "pending"
            );

            SessionState.SetString(
                JobDiagnosticsKey,
                ""
            );

            if (attempts == 0)
            {
                // Importing the new .cs asset normally schedules
                // compilation by itself. Avoid a synchronous import
                // followed by EditorApplication.isCompiling: Unity 6
                // may still be mutating its assembly-builder list.
                AssetDatabase.ImportAsset(
                    assetPath,
                    ImportAssetOptions.ForceUpdate
                );
            }
            else
            {
                // Fallback only when no compilationStarted event
                // arrived after the import. This runs from a later
                // Editor delayCall on Unity's main thread.
                CompilationPipeline.RequestScriptCompilation();
            }
        }
        catch (Exception ex)
        {
            SessionState.SetString(
                JobStateKey,
                "failed"
            );

            SessionState.SetString(
                JobDiagnosticsKey,
                ex.GetType().Name + ": " + ex.Message
            );
        }
    }


    private static void ScheduleCompilation(
        string jobId,
        string assetPath
    )
    {
        if (
            string.IsNullOrWhiteSpace(jobId)
            || SessionState.GetBool(
                JobCompilationScheduledKey,
                false
            )
        )
        {
            return;
        }

        SessionState.SetBool(
            JobCompilationScheduledKey,
            true
        );

        EditorApplication.delayCall +=
            () =>
            {
                SessionState.SetBool(
                    JobCompilationScheduledKey,
                    false
                );

                BeginCompilation(
                    jobId,
                    assetPath
                );
            };
    }


    private static void OnCompilationStarted(object context)
    {
        string jobId =
            SessionState.GetString(JobIdKey, "");

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        string state =
            SessionState.GetString(JobStateKey, "");

        if (
            state != "pending"
            && state != "compiling"
        )
        {
            return;
        }

        currentCompilerErrors.Clear();

        SessionState.SetBool(
            JobCompilationStartedKey,
            true
        );

        SessionState.SetString(
            JobStateKey,
            "compiling"
        );

        SessionState.SetString(
            JobDiagnosticsKey,
            ""
        );
    }


    private static void OnAssemblyCompilationFinished(
        string assemblyPath,
        CompilerMessage[] compilerMessages
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                SessionState.GetString(JobIdKey, "")
            )
        )
        {
            return;
        }

        string state =
            SessionState.GetString(JobStateKey, "");

        if (
            state != "pending"
            && state != "compiling"
        )
        {
            return;
        }

        foreach (
            CompilerMessage message
            in compilerMessages ?? Array.Empty<CompilerMessage>()
        )
        {
            if (message.type != CompilerMessageType.Error)
            {
                continue;
            }

            currentCompilerErrors.Add(
                FormatCompilerMessage(message)
            );
        }
    }


    private static void OnCompilationFinished(object context)
    {
        string jobId =
            SessionState.GetString(JobIdKey, "");

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        string state =
            SessionState.GetString(JobStateKey, "");

        if (
            state != "pending"
            && state != "compiling"
        )
        {
            return;
        }

        string diagnostics =
            string.Join(
                "\n",
                currentCompilerErrors
                    .Distinct()
                    .Take(16)
            );

        SessionState.SetString(
            JobDiagnosticsKey,
            diagnostics
        );

        SessionState.SetString(
            JobStateKey,
            string.IsNullOrWhiteSpace(diagnostics)
                ? "compiled"
                : "failed"
        );
    }


    private static void RecoverJobAfterReload()
    {
        string state =
            SessionState.GetString(JobStateKey, "");

        if (
            state != "pending"
            && state != "compiling"
        )
        {
            return;
        }

        bool compilationStarted =
            SessionState.GetBool(
                JobCompilationStartedKey,
                false
            );

        string className =
            SessionState.GetString(JobClassNameKey, "");

        if (
            compilationStarted
            && IsCompiledMonoBehaviourLoaded(
                className
            )
        )
        {
            SessionState.SetString(
                JobStateKey,
                "compiled"
            );

            SessionState.SetString(
                JobDiagnosticsKey,
                ""
            );

            return;
        }

        if (state == "pending")
        {
            string jobId =
                SessionState.GetString(JobIdKey, "");

            string assetPath =
                SessionState.GetString(JobAssetPathKey, "");

            SessionState.SetBool(
                JobCompilationScheduledKey,
                false
            );

            ScheduleCompilation(
                jobId,
                assetPath
            );
        }
    }


    private static bool IsCompiledMonoBehaviourLoaded(
        string className
    )
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return
            TypeCache
                .GetTypesDerivedFrom<MonoBehaviour>()
                .Any(type =>
                    string.Equals(
                        type.Name,
                        className,
                        StringComparison.Ordinal
                    )
                    || string.Equals(
                        type.FullName,
                        className,
                        StringComparison.Ordinal
                    )
                );
    }


    private static double GetSecondsSinceLastCompileRequest()
    {
        string ticksText =
            SessionState.GetString(
                JobLastCompileRequestTicksKey,
                ""
            );

        if (
            !long.TryParse(
                ticksText,
                out long ticks
            )
            || ticks <= 0
        )
        {
            return double.MaxValue;
        }

        try
        {
            return
                Math.Max(
                    0.0,
                    (DateTime.UtcNow - new DateTime(
                        ticks,
                        DateTimeKind.Utc
                    )).TotalSeconds
                );
        }
        catch
        {
            return double.MaxValue;
        }
    }


    private static string ValidateCreateRequest(
        CreateScriptRequest request
    )
    {
        if (request == null)
        {
            return "Request body is invalid.";
        }

        if (
            string.IsNullOrWhiteSpace(request.className)
            || !System.Text.RegularExpressions.Regex.IsMatch(
                request.className,
                "^[A-Za-z_][A-Za-z0-9_]{0,127}$"
            )
        )
        {
            return "className is invalid.";
        }

        string assetPath =
            NormalizeAssetPath(request.assetPath);

        if (
            !assetPath.StartsWith(
                "Assets/Scripts/",
                StringComparison.Ordinal
            )
            || !assetPath.EndsWith(
                ".cs",
                StringComparison.OrdinalIgnoreCase
            )
            || assetPath.Contains("..", StringComparison.Ordinal)
            || assetPath.Contains(':')
        )
        {
            return
                "assetPath must be a safe .cs path inside Assets/Scripts/.";
        }

        string expectedFileName =
            request.className + ".cs";

        if (
            !string.Equals(
                Path.GetFileName(assetPath),
                expectedFileName,
                StringComparison.Ordinal
            )
        )
        {
            return
                "Script filename must exactly match className ("
                + expectedFileName
                + ").";
        }

        if (string.IsNullOrWhiteSpace(request.source))
        {
            return "Script source is empty.";
        }

        if (request.source.Length > MaxSourceChars)
        {
            return
                "Script source is too large ("
                + request.source.Length
                + "/"
                + MaxSourceChars
                + " characters).";
        }

        if (
            request.source.IndexOf(
                "MonoBehaviour",
                StringComparison.Ordinal
            )
            < 0
        )
        {
            return
                "Persistent runtime script must derive from MonoBehaviour.";
        }

        if (
            request.source.IndexOf(
                "class " + request.className,
                StringComparison.Ordinal
            )
            < 0
        )
        {
            return
                "Source must declare class "
                + request.className
                + ".";
        }

        foreach (string blocked in BlockedSourceFragments)
        {
            if (
                request.source.IndexOf(
                    blocked,
                    StringComparison.OrdinalIgnoreCase
                )
                >= 0
            )
            {
                return
                    "Blocked API/token '"
                    + blocked
                    + "' in persistent runtime script.";
            }
        }

        return null;
    }


    private static void BackupExistingScript(
        string projectRoot,
        string assetPath,
        string absolutePath
    )
    {
        string backupRoot =
            Path.Combine(
                projectRoot,
                "Library",
                "AIUnityBridge",
                "ScriptBackups"
            );

        Directory.CreateDirectory(backupRoot);

        string backupName =
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
            + "-"
            + Path.GetFileName(assetPath);

        File.Copy(
            absolutePath,
            Path.Combine(backupRoot, backupName),
            overwrite: false
        );
    }


    private static void SetJob(
        string jobId,
        string state,
        string assetPath,
        string className,
        string diagnostics
    )
    {
        SessionState.SetString(JobIdKey, jobId);
        SessionState.SetString(JobStateKey, state);
        SessionState.SetString(JobAssetPathKey, assetPath);
        SessionState.SetString(JobClassNameKey, className);
        SessionState.SetString(JobDiagnosticsKey, diagnostics);
        SessionState.SetInt(JobCompileAttemptsKey, 0);
        SessionState.SetString(JobLastCompileRequestTicksKey, "");
        SessionState.SetBool(JobCompilationScheduledKey, false);
        SessionState.SetBool(JobCompilationStartedKey, false);
    }


    private static string FormatCompilerMessage(
        CompilerMessage message
    )
    {
        return
            Path.GetFileName(message.file)
            + " L"
            + message.line
            + ":"
            + message.column
            + " "
            + message.message;
    }


    private static string NormalizeAssetPath(string assetPath)
    {
        return
            (assetPath ?? "")
                .Replace('\\', '/')
                .Trim();
    }


    private static string NormalizeNewLines(string value)
    {
        return
            (value ?? "")
                .Replace("\r\n", "\n")
                .Trim();
    }


    private static ScriptResponse Failure(
        string phase,
        string message
    )
    {
        return
            new ScriptResponse
            {
                success = false,
                phase = phase,
                state = "failed",
                message = message
            };
    }


    private static void WriteResponse(
        HttpListenerContext context,
        int statusCode,
        ScriptResponse response
    )
    {
        try
        {
            string json =
                JsonUtility.ToJson(response);

            byte[] bytes =
                Encoding.UTF8.GetBytes(json);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType =
                "application/json; charset=utf-8";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;

            context.Response.OutputStream.Write(
                bytes,
                0,
                bytes.Length
            );
        }
        catch
        {
            // External client disconnected.
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
                context.Response.Close();
            }
            catch
            {
                // Response already closed.
            }
        }
    }


    private enum RequestKind
    {
        Create,
        Status
    }


    [Serializable]
    private sealed class CreateScriptRequest
    {
        public string assetPath;
        public string className;
        public string source;
        public bool overwrite;
    }


    [Serializable]
    private sealed class ScriptResponse
    {
        public bool success;
        public string phase;
        public string state;
        public string jobId;
        public string assetPath;
        public string className;
        public string message;
        public string diagnostics;
    }


    private sealed class PendingRequest
    {
        public RequestKind kind;
        public HttpListenerContext context;
        public CreateScriptRequest createRequest;
        public string jobId;

        public static PendingRequest ForCreate(
            HttpListenerContext context,
            CreateScriptRequest request
        )
        {
            return
                new PendingRequest
                {
                    kind = RequestKind.Create,
                    context = context,
                    createRequest = request
                };
        }

        public static PendingRequest ForStatus(
            HttpListenerContext context,
            string jobId
        )
        {
            return
                new PendingRequest
                {
                    kind = RequestKind.Status,
                    context = context,
                    jobId = jobId
                };
        }
    }
}
