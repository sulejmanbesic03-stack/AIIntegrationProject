using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

using UnityEditor;

using UnityEngine;
using UnityEngine.SceneManagement;


[InitializeOnLoad]
public static class UnityCodeIntelligenceServer
{
    private const string Prefix =
        "http://127.0.0.1:47827/";

    private const string RequiredHeader =
        "AI-Assistant-Local";

    private const int MaxScriptChars =
        50000;

    private const int MaxReadLines =
        220;

    private static readonly ConcurrentQueue<PendingRequest>
        pendingRequests =
            new ConcurrentQueue<PendingRequest>();

    private static HttpListener listener;
    private static Thread listenerThread;
    private static volatile bool running;


    static UnityCodeIntelligenceServer()
    {
        EditorApplication.update += ProcessPendingRequests;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        EditorApplication.delayCall += Start;
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
                    Name = "AI Unity Code Intelligence Server"
                };

            listenerThread.Start();

            Debug.Log(
                "[AI Code Intelligence] Listening on 127.0.0.1:47827"
            );
        }
        catch (Exception ex)
        {
            running = false;

            Debug.LogError(
                "[AI Code Intelligence] Start failed: "
                + ex.Message
            );
        }
    }


    private static void Stop()
    {
        running = false;

        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch
        {
            // Unity is closing or reloading editor assemblies.
        }
    }


    private static void ListenLoop()
    {
        while (running)
        {
            try
            {
                HttpListenerContext context =
                    listener.GetContext();

                QueueRequest(context);
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogError(
                        "[AI Code Intelligence] Listener error: "
                        + ex.Message
                    );
                }
            }
        }
    }


    private static void QueueRequest(
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
                    Failure(
                        "authorization",
                        "Unauthorized bridge request."
                    )
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
                && path == "/project-settings"
            )
            {
                pendingRequests.Enqueue(
                    PendingRequest.ForSimple(
                        RequestKind.ProjectSettings,
                        context
                    )
                );
                return;
            }

            if (
                context.Request.HttpMethod == "GET"
                && path == "/list-scripts"
            )
            {
                pendingRequests.Enqueue(
                    PendingRequest.ForValue(
                        RequestKind.ListScripts,
                        context,
                        context.Request.QueryString["searchText"] ?? ""
                    )
                );
                return;
            }

            if (
                context.Request.HttpMethod == "GET"
                && path == "/read-script"
            )
            {
                pendingRequests.Enqueue(
                    PendingRequest.ForRead(
                        context,
                        context.Request.QueryString["assetPath"] ?? "",
                        ParseInt(
                            context.Request.QueryString["startLine"],
                            1
                        ),
                        ParseInt(
                            context.Request.QueryString["endLine"],
                            0
                        )
                    )
                );
                return;
            }

            if (
                context.Request.HttpMethod == "GET"
                && path == "/runtime-state"
            )
            {
                pendingRequests.Enqueue(
                    PendingRequest.ForValue(
                        RequestKind.RuntimeState,
                        context,
                        context.Request.QueryString["objectPath"] ?? ""
                    )
                );
                return;
            }

            if (
                context.Request.HttpMethod == "POST"
                && path == "/review-script"
            )
            {
                string body = ReadBody(context);

                ScriptPathRequest request =
                    JsonUtility.FromJson<ScriptPathRequest>(body);

                pendingRequests.Enqueue(
                    PendingRequest.ForValue(
                        RequestKind.ReviewScript,
                        context,
                        request != null
                            ? request.assetPath
                            : ""
                    )
                );
                return;
            }

            if (
                context.Request.HttpMethod == "POST"
                && path == "/play-mode"
            )
            {
                string body = ReadBody(context);

                PlayModeRequest request =
                    JsonUtility.FromJson<PlayModeRequest>(body);

                pendingRequests.Enqueue(
                    PendingRequest.ForValue(
                        RequestKind.PlayMode,
                        context,
                        request != null
                            ? request.action
                            : ""
                    )
                );
                return;
            }

            WriteResponse(
                context,
                404,
                Failure(
                    "request",
                    "Unknown code-intelligence endpoint."
                )
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


    private static string ReadBody(
        HttpListenerContext context
    )
    {
        using (
            StreamReader reader =
                new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding
                )
        )
        {
            return reader.ReadToEnd();
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
            switch (pending.kind)
            {
                case RequestKind.ProjectSettings:
                    HandleProjectSettings(pending);
                    break;

                case RequestKind.ListScripts:
                    HandleListScripts(pending);
                    break;

                case RequestKind.ReadScript:
                    HandleReadScript(pending);
                    break;

                case RequestKind.ReviewScript:
                    HandleReviewScript(pending);
                    break;

                case RequestKind.RuntimeState:
                    HandleRuntimeState(pending);
                    break;

                case RequestKind.PlayMode:
                    HandlePlayMode(pending);
                    break;
            }
        }
    }


    private static void HandleProjectSettings(
        PendingRequest pending
    )
    {
        bool newInputSystem = IsNewInputSystemEnabled();
        bool legacyInput = IsLegacyInputEnabled();

        string inputHandling =
            newInputSystem && legacyInput
                ? "Both"
                : newInputSystem
                    ? "Input System Package (New)"
                    : legacyInput
                        ? "Input Manager (Old)"
                        : "Unknown";

        Scene scene =
            SceneManager.GetActiveScene();

        WriteResponse(
            pending.context,
            200,
            new CodeIntelligenceResponse
            {
                success = true,
                phase = "project-settings",
                message =
                    "Unity project settings inspected successfully.",
                unityVersion = Application.unityVersion,
                inputHandling = inputHandling,
                newInputSystemEnabled = newInputSystem,
                legacyInputEnabled = legacyInput,
                isPlaying = EditorApplication.isPlaying,
                isCompiling = EditorApplication.isCompiling,
                activeSceneName = scene.name,
                activeScenePath = scene.path
            }
        );
    }


    private static void HandleListScripts(
        PendingRequest pending
    )
    {
        try
        {
            string projectRoot =
                Directory.GetCurrentDirectory();

            string scriptsRoot =
                Path.Combine(
                    projectRoot,
                    "Assets",
                    "Scripts"
                );

            if (!Directory.Exists(scriptsRoot))
            {
                WriteResponse(
                    pending.context,
                    200,
                    new CodeIntelligenceResponse
                    {
                        success = true,
                        phase = "list-scripts",
                        message = "Assets/Scripts does not exist yet.",
                        scripts = Array.Empty<string>()
                    }
                );
                return;
            }

            string searchText =
                (pending.value ?? "").Trim();

            string[] scripts =
                Directory
                    .GetFiles(
                        scriptsRoot,
                        "*.cs",
                        SearchOption.AllDirectories
                    )
                    .Select(path =>
                        ToAssetPath(
                            projectRoot,
                            path
                        )
                    )
                    .Where(path =>
                        string.IsNullOrWhiteSpace(searchText)
                        || path.IndexOf(
                            searchText,
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0
                    )
                    .OrderBy(path => path)
                    .Take(60)
                    .ToArray();

            WriteResponse(
                pending.context,
                200,
                new CodeIntelligenceResponse
                {
                    success = true,
                    phase = "list-scripts",
                    message =
                        "Found " + scripts.Length + " persistent script(s).",
                    scripts = scripts
                }
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                pending.context,
                500,
                Failure(
                    "list-scripts",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
    }


    private static void HandleReadScript(
        PendingRequest pending
    )
    {
        string normalizedAssetPath;
        string absolutePath;

        string validationError =
            ResolveScriptPath(
                pending.assetPath,
                out normalizedAssetPath,
                out absolutePath
            );

        if (validationError != null)
        {
            WriteResponse(
                pending.context,
                400,
                Failure("read-script", validationError)
            );
            return;
        }

        try
        {
            string source =
                File.ReadAllText(absolutePath);

            if (source.Length > MaxScriptChars)
            {
                WriteResponse(
                    pending.context,
                    413,
                    Failure(
                        "read-script",
                        "Script exceeds the safe read limit of "
                        + MaxScriptChars
                        + " characters."
                    )
                );
                return;
            }

            string[] lines =
                NormalizeNewLines(source)
                    .Split('\n');

            int startLine =
                Math.Max(1, pending.startLine);

            int requestedEnd =
                pending.endLine <= 0
                    ? Math.Min(
                        lines.Length,
                        startLine + MaxReadLines - 1
                    )
                    : pending.endLine;

            int endLine =
                Math.Min(
                    lines.Length,
                    requestedEnd
                );

            if (
                startLine > lines.Length
                || endLine < startLine
                || endLine - startLine + 1 > MaxReadLines
            )
            {
                WriteResponse(
                    pending.context,
                    400,
                    Failure(
                        "read-script",
                        "Requested line range is invalid or exceeds "
                        + MaxReadLines
                        + " lines."
                    )
                );
                return;
            }

            string section =
                string.Join(
                    "\n",
                    lines.Skip(startLine - 1)
                        .Take(endLine - startLine + 1)
                );

            WriteResponse(
                pending.context,
                200,
                new CodeIntelligenceResponse
                {
                    success = true,
                    phase = "read-script",
                    message =
                        "Read "
                        + normalizedAssetPath
                        + " lines "
                        + startLine
                        + "-"
                        + endLine
                        + " of "
                        + lines.Length
                        + ".",
                    assetPath = normalizedAssetPath,
                    source = section,
                    startLine = startLine,
                    endLine = endLine,
                    totalLines = lines.Length,
                    truncated = endLine < lines.Length
                }
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                pending.context,
                500,
                Failure(
                    "read-script",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
    }


    private static void HandleReviewScript(
        PendingRequest pending
    )
    {
        string normalizedAssetPath;
        string absolutePath;

        string validationError =
            ResolveScriptPath(
                pending.value,
                out normalizedAssetPath,
                out absolutePath
            );

        if (validationError != null)
        {
            WriteResponse(
                pending.context,
                400,
                Failure("review-script", validationError)
            );
            return;
        }

        try
        {
            string source =
                File.ReadAllText(absolutePath);

            List<ReviewIssue> issues =
                ReviewSource(source);

            int errorCount =
                issues.Count(issue => issue.severity == "error");

            int warningCount =
                issues.Count(issue => issue.severity == "warning");

            WriteResponse(
                pending.context,
                200,
                new CodeIntelligenceResponse
                {
                    success = true,
                    phase = "review-script",
                    assetPath = normalizedAssetPath,
                    message =
                        issues.Count == 0
                            ? "No known Unity gameplay-code risks were detected."
                            : "Review found "
                                + errorCount
                                + " error(s) and "
                                + warningCount
                                + " warning(s).",
                    issueCount = issues.Count,
                    errorCount = errorCount,
                    warningCount = warningCount,
                    issues = issues.ToArray()
                }
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                pending.context,
                500,
                Failure(
                    "review-script",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
    }


    private static List<ReviewIssue> ReviewSource(
        string source
    )
    {
        List<ReviewIssue> issues =
            new List<ReviewIssue>();

        bool usesLegacyInput =
            ContainsAny(
                source,
                "Input.GetAxis",
                "Input.GetButton",
                "Input.GetKey",
                "Input.GetMouseButton"
            );

        bool usesNewInput =
            ContainsAny(
                source,
                "UnityEngine.InputSystem",
                "Keyboard.current",
                "Mouse.current",
                "PlayerInput"
            );

        if (
            usesLegacyInput
            && IsNewInputSystemEnabled()
            && !IsLegacyInputEnabled()
        )
        {
            AddIssue(
                issues,
                "error",
                "INPUT_BACKEND_MISMATCH",
                "Script uses UnityEngine.Input, but the project has only the new Input System enabled.",
                "Use UnityEngine.InputSystem or support both backends with ENABLE_INPUT_SYSTEM."
            );
        }

        if (
            usesNewInput
            && !IsNewInputSystemEnabled()
        )
        {
            AddIssue(
                issues,
                "error",
                "NEW_INPUT_DISABLED",
                "Script uses the new Input System, but it is not enabled for this project.",
                "Enable the Input System package or use the legacy Input API."
            );
        }

        bool usesRigidbody =
            source.IndexOf(
                "Rigidbody",
                StringComparison.Ordinal
            ) >= 0;

        if (
            usesRigidbody
            && ContainsAny(
                source,
                "transform.Rotate(",
                "transform.rotation =",
                "transform.eulerAngles ="
            )
        )
        {
            AddIssue(
                issues,
                "warning",
                "RIGIDBODY_TRANSFORM_ROTATION",
                "A Rigidbody-controlled object is rotated directly through Transform.",
                "Read look input in Update, then rotate the Rigidbody with MoveRotation in FixedUpdate."
            );
        }

        if (
            usesRigidbody
            && ContainsAny(
                source,
                "transform.position =",
                "transform.Translate("
            )
        )
        {
            AddIssue(
                issues,
                "warning",
                "RIGIDBODY_TRANSFORM_MOVEMENT",
                "A Rigidbody-controlled object is moved directly through Transform.",
                "Use Rigidbody velocity or MovePosition from FixedUpdate."
            );
        }

        if (
            ContainsAny(
                source,
                ".AddForce(",
                ".linearVelocity =",
                ".MovePosition(",
                ".MoveRotation("
            )
            && source.IndexOf(
                "FixedUpdate",
                StringComparison.Ordinal
            ) < 0
        )
        {
            AddIssue(
                issues,
                "warning",
                "PHYSICS_WITHOUT_FIXED_UPDATE",
                "Physics mutations are present, but FixedUpdate was not found.",
                "Apply Rigidbody movement and forces in FixedUpdate."
            );
        }

        if (
            MethodBodyContains(
                source,
                "FixedUpdate",
                "Input."
            )
            || MethodBodyContains(
                source,
                "FixedUpdate",
                "Keyboard.current"
            )
        )
        {
            AddIssue(
                issues,
                "warning",
                "INPUT_IN_FIXED_UPDATE",
                "Input is read directly in FixedUpdate and short button presses may be missed.",
                "Read and cache input in Update; consume the cached values in FixedUpdate."
            );
        }

        if (
            usesRigidbody
            && source.IndexOf(
                "RequireComponent",
                StringComparison.Ordinal
            ) < 0
        )
        {
            AddIssue(
                issues,
                "warning",
                "MISSING_REQUIRE_COMPONENT",
                "The script depends on Rigidbody but does not declare RequireComponent.",
                "Add [RequireComponent(typeof(Rigidbody))] and the required Collider type."
            );
        }

        if (
            source.IndexOf(
                "transform.Find(\"Head\")",
                StringComparison.Ordinal
            ) >= 0
            && source.IndexOf(
                "GetComponentInChildren<Camera>",
                StringComparison.Ordinal
            ) < 0
        )
        {
            AddIssue(
                issues,
                "warning",
                "FRAGILE_CAMERA_LOOKUP",
                "Camera lookup depends only on a child named Head.",
                "Expose a serialized camera pivot and add a GetComponentInChildren<Camera> fallback."
            );
        }

        if (
            source.IndexOf(
                "groundCheckDistance = 0.1f",
                StringComparison.OrdinalIgnoreCase
            ) >= 0
            && source.IndexOf(
                "bounds.extents",
                StringComparison.Ordinal
            ) < 0
        )
        {
            AddIssue(
                issues,
                "warning",
                "GROUND_CHECK_FROM_CENTER",
                "Ground-check distance appears too short for a ray starting at the player origin.",
                "Calculate the cast distance from Collider.bounds or use a dedicated ground-check Transform."
            );
        }

        return issues;
    }


    private static void HandleRuntimeState(
        PendingRequest pending
    )
    {
        string objectPath =
            (pending.value ?? "").Trim();

        if (string.IsNullOrWhiteSpace(objectPath))
        {
            WriteResponse(
                pending.context,
                400,
                Failure(
                    "runtime-state",
                    "objectPath is required."
                )
            );
            return;
        }

        GameObject target =
            FindByHierarchyPath(objectPath);

        if (target == null)
        {
            WriteResponse(
                pending.context,
                404,
                Failure(
                    "runtime-state",
                    "GameObject not found: " + objectPath
                )
            );
            return;
        }

        Component[] components =
            target.GetComponents<Component>();

        Rigidbody body =
            target.GetComponent<Rigidbody>();

        Collider[] colliders =
            target.GetComponents<Collider>();

        Camera camera =
            target.GetComponentInChildren<Camera>(true);

        RuntimeRigidbodyInfo bodyInfo =
            null;

        if (body != null)
        {
            bodyInfo =
                new RuntimeRigidbodyInfo
                {
                    mass = body.mass,
                    useGravity = body.useGravity,
                    isKinematic = body.isKinematic,
                    velocity = body.linearVelocity,
                    angularVelocity = body.angularVelocity,
                    constraints = body.constraints.ToString()
                };
        }

        WriteResponse(
            pending.context,
            200,
            new CodeIntelligenceResponse
            {
                success = true,
                phase = "runtime-state",
                message =
                    "Runtime state captured for "
                    + GetHierarchyPath(target.transform)
                    + ".",
                objectPath = GetHierarchyPath(target.transform),
                isPlaying = EditorApplication.isPlaying,
                activeSelf = target.activeSelf,
                activeInHierarchy = target.activeInHierarchy,
                position = target.transform.position,
                rotation = target.transform.eulerAngles,
                localScale = target.transform.localScale,
                components = components
                    .Where(component => component != null)
                    .Select(component => component.GetType().FullName)
                    .ToArray(),
                colliderCount = colliders.Length,
                cameraFound = camera != null,
                cameraPath = camera != null
                    ? GetHierarchyPath(camera.transform)
                    : "",
                rigidbody = bodyInfo
            }
        );
    }


    private static void HandlePlayMode(
        PendingRequest pending
    )
    {
        string action =
            (pending.value ?? "")
                .Trim()
                .ToLowerInvariant();

        if (
            action != "enter"
            && action != "exit"
        )
        {
            WriteResponse(
                pending.context,
                400,
                Failure(
                    "play-mode",
                    "action must be 'enter' or 'exit'."
                )
            );
            return;
        }

        if (EditorApplication.isCompiling)
        {
            WriteResponse(
                pending.context,
                409,
                Failure(
                    "play-mode",
                    "Unity is compiling. Wait for compilation before changing Play Mode."
                )
            );
            return;
        }

        bool enter =
            action == "enter";

        if (EditorApplication.isPlaying == enter)
        {
            WriteResponse(
                pending.context,
                200,
                new CodeIntelligenceResponse
                {
                    success = true,
                    phase = "play-mode",
                    message = enter
                        ? "Unity is already in Play Mode."
                        : "Unity is already outside Play Mode.",
                    isPlaying = EditorApplication.isPlaying
                }
            );
            return;
        }

        WriteResponse(
            pending.context,
            200,
            new CodeIntelligenceResponse
            {
                success = true,
                phase = "play-mode",
                message = enter
                    ? "Entering Play Mode was scheduled."
                    : "Exiting Play Mode was scheduled.",
                isPlaying = EditorApplication.isPlaying
            }
        );

        EditorApplication.delayCall +=
            () =>
            {
                EditorApplication.isPlaying = enter;
            };
    }


    private static string ResolveScriptPath(
        string assetPath,
        out string normalizedAssetPath,
        out string absolutePath
    )
    {
        normalizedAssetPath =
            NormalizeAssetPath(assetPath);

        absolutePath = "";

        if (
            !normalizedAssetPath.StartsWith(
                "Assets/Scripts/",
                StringComparison.Ordinal
            )
            || !normalizedAssetPath.EndsWith(
                ".cs",
                StringComparison.OrdinalIgnoreCase
            )
            || normalizedAssetPath.Contains("..")
            || normalizedAssetPath.Contains(":")
        )
        {
            return
                "assetPath must be a safe .cs path inside Assets/Scripts/.";
        }

        string projectRoot =
            Path.GetFullPath(
                Directory.GetCurrentDirectory()
            );

        absolutePath =
            Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    normalizedAssetPath
                )
            );

        string scriptsRoot =
            Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    "Assets",
                    "Scripts"
                )
            );

        if (
            !absolutePath.StartsWith(
                scriptsRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return "Resolved script path escaped Assets/Scripts/.";
        }

        if (!File.Exists(absolutePath))
        {
            return "Persistent script was not found: " + normalizedAssetPath;
        }

        return null;
    }


    private static GameObject FindByHierarchyPath(
        string hierarchyPath
    )
    {
        string normalized =
            (hierarchyPath ?? "")
                .Replace('\\', '/')
                .Trim('/');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        string[] parts =
            normalized.Split('/');

        Scene scene =
            SceneManager.GetActiveScene();

        GameObject root =
            scene
                .GetRootGameObjects()
                .FirstOrDefault(item =>
                    string.Equals(
                        item.name,
                        parts[0],
                        StringComparison.Ordinal
                    )
                );

        if (root == null)
        {
            return null;
        }

        Transform current =
            root.transform;

        for (int index = 1; index < parts.Length; index++)
        {
            Transform next = null;

            for (
                int childIndex = 0;
                childIndex < current.childCount;
                childIndex++
            )
            {
                Transform child =
                    current.GetChild(childIndex);

                if (
                    string.Equals(
                        child.name,
                        parts[index],
                        StringComparison.Ordinal
                    )
                )
                {
                    next = child;
                    break;
                }
            }

            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current.gameObject;
    }


    private static string GetHierarchyPath(
        Transform transform
    )
    {
        List<string> parts =
            new List<string>();

        Transform current =
            transform;

        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();

        return string.Join("/", parts);
    }


    private static bool MethodBodyContains(
        string source,
        string methodName,
        string token
    )
    {
        int methodIndex =
            source.IndexOf(
                methodName + "(",
                StringComparison.Ordinal
            );

        if (methodIndex < 0)
        {
            return false;
        }

        int openBrace =
            source.IndexOf('{', methodIndex);

        if (openBrace < 0)
        {
            return false;
        }

        int depth = 0;

        for (int index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;

                if (depth == 0)
                {
                    string body =
                        source.Substring(
                            openBrace,
                            index - openBrace + 1
                        );

                    return body.IndexOf(
                        token,
                        StringComparison.Ordinal
                    ) >= 0;
                }
            }
        }

        return false;
    }


    private static void AddIssue(
        List<ReviewIssue> issues,
        string severity,
        string code,
        string message,
        string suggestion
    )
    {
        issues.Add(
            new ReviewIssue
            {
                severity = severity,
                code = code,
                message = message,
                suggestion = suggestion
            }
        );
    }


    private static bool ContainsAny(
        string source,
        params string[] values
    )
    {
        foreach (string value in values)
        {
            if (
                source.IndexOf(
                    value,
                    StringComparison.Ordinal
                ) >= 0
            )
            {
                return true;
            }
        }

        return false;
    }


    private static bool IsNewInputSystemEnabled()
    {
#if ENABLE_INPUT_SYSTEM
        return true;
#else
        return false;
#endif
    }


    private static bool IsLegacyInputEnabled()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        return true;
#else
        return false;
#endif
    }


    private static string ToAssetPath(
        string projectRoot,
        string absolutePath
    )
    {
        return
            absolutePath
                .Substring(projectRoot.Length)
                .TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                )
                .Replace('\\', '/');
    }


    private static string NormalizeAssetPath(
        string assetPath
    )
    {
        return
            (assetPath ?? "")
                .Replace('\\', '/')
                .Trim();
    }


    private static string NormalizeNewLines(
        string value
    )
    {
        return
            (value ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
    }


    private static int ParseInt(
        string value,
        int fallback
    )
    {
        return int.TryParse(value, out int result)
            ? result
            : fallback;
    }


    private static CodeIntelligenceResponse Failure(
        string phase,
        string message
    )
    {
        return
            new CodeIntelligenceResponse
            {
                success = false,
                phase = phase,
                message = message
            };
    }


    private static void WriteResponse(
        HttpListenerContext context,
        int statusCode,
        CodeIntelligenceResponse response
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
        ProjectSettings,
        ListScripts,
        ReadScript,
        ReviewScript,
        RuntimeState,
        PlayMode
    }


    [Serializable]
    private sealed class ScriptPathRequest
    {
        public string assetPath;
    }


    [Serializable]
    private sealed class PlayModeRequest
    {
        public string action;
    }


    [Serializable]
    private sealed class ReviewIssue
    {
        public string severity;
        public string code;
        public string message;
        public string suggestion;
    }


    [Serializable]
    private sealed class RuntimeRigidbodyInfo
    {
        public float mass;
        public bool useGravity;
        public bool isKinematic;
        public Vector3 velocity;
        public Vector3 angularVelocity;
        public string constraints;
    }


    [Serializable]
    private sealed class CodeIntelligenceResponse
    {
        public bool success;
        public string phase;
        public string message;

        public string unityVersion;
        public string inputHandling;
        public bool newInputSystemEnabled;
        public bool legacyInputEnabled;
        public bool isPlaying;
        public bool isCompiling;
        public string activeSceneName;
        public string activeScenePath;

        public string[] scripts;
        public string assetPath;
        public string source;
        public int startLine;
        public int endLine;
        public int totalLines;
        public bool truncated;

        public int issueCount;
        public int errorCount;
        public int warningCount;
        public ReviewIssue[] issues;

        public string objectPath;
        public bool activeSelf;
        public bool activeInHierarchy;
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 localScale;
        public string[] components;
        public int colliderCount;
        public bool cameraFound;
        public string cameraPath;
        public RuntimeRigidbodyInfo rigidbody;
    }


    private sealed class PendingRequest
    {
        public RequestKind kind;
        public HttpListenerContext context;
        public string value;
        public string assetPath;
        public int startLine;
        public int endLine;

        public static PendingRequest ForSimple(
            RequestKind kind,
            HttpListenerContext context
        )
        {
            return
                new PendingRequest
                {
                    kind = kind,
                    context = context
                };
        }

        public static PendingRequest ForValue(
            RequestKind kind,
            HttpListenerContext context,
            string value
        )
        {
            return
                new PendingRequest
                {
                    kind = kind,
                    context = context,
                    value = value
                };
        }

        public static PendingRequest ForRead(
            HttpListenerContext context,
            string assetPath,
            int startLine,
            int endLine
        )
        {
            return
                new PendingRequest
                {
                    kind = RequestKind.ReadScript,
                    context = context,
                    assetPath = assetPath,
                    startLine = startLine,
                    endLine = endLine
                };
        }
    }
}
