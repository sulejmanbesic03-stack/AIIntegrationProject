using System;
using System.IO;
using System.Linq;

using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

[InitializeOnLoad]
public static class UnityPersistentScriptWatchdog
{
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

    private const string JobCompilationStartedKey =
        "AI.PersistentScript.CompilationStarted";

    private const string WatchdogLastKickTicksKey =
        "AI.PersistentScript.WatchdogLastKickTicks";

    private const double KickIntervalSeconds =
        3.0;

    static UnityPersistentScriptWatchdog()
    {
        EditorApplication.update += Tick;
        AssemblyReloadEvents.afterAssemblyReload += RecoverAfterReload;
        EditorApplication.delayCall += RecoverAfterReload;
    }

    private static void Tick()
    {
        string jobId =
            SessionState.GetString(JobIdKey, "");

        string state =
            SessionState.GetString(JobStateKey, "");

        if (
            string.IsNullOrWhiteSpace(jobId)
            || !IsActiveState(state)
        )
        {
            return;
        }

        if (EditorApplication.isCompiling)
        {
            SessionState.SetBool(
                JobCompilationStartedKey,
                true
            );

            SessionState.SetString(
                JobStateKey,
                "compiling"
            );

            return;
        }

        // Do not trust a stale loaded class alone. The source file must have a
        // compiled assembly on disk that is at least as new as the written .cs
        // file. This avoids falsely accepting an old class when repairing an
        // existing script.
        if (HasFreshCompiledArtifact())
        {
            MarkCompiled();
            return;
        }

        // Unity 6 can occasionally miss/delay the normal compilationStarted
        // event around AssetDatabase import + domain reload. Give the editor a
        // deterministic nudge instead of leaving Agent V2 polling for 90 sec.
        if (
            state == "pending"
            && SecondsSinceWatchdogKick() >= KickIntervalSeconds
        )
        {
            KickCompilation();
        }
    }

    private static void RecoverAfterReload()
    {
        string state =
            SessionState.GetString(JobStateKey, "");

        if (!IsActiveState(state))
        {
            return;
        }

        if (HasFreshCompiledArtifact())
        {
            MarkCompiled();
        }
    }

    private static bool HasFreshCompiledArtifact()
    {
        string assetPath =
            SessionState.GetString(JobAssetPathKey, "");

        string className =
            SessionState.GetString(JobClassNameKey, "");

        if (
            string.IsNullOrWhiteSpace(assetPath)
            || string.IsNullOrWhiteSpace(className)
            || !IsCompiledMonoBehaviourLoaded(className)
        )
        {
            return false;
        }

        try
        {
            string projectRoot =
                Directory.GetCurrentDirectory();

            string sourcePath =
                Path.GetFullPath(
                    Path.Combine(projectRoot, assetPath)
                );

            if (!File.Exists(sourcePath))
            {
                return false;
            }

            string assemblyName =
                CompilationPipeline.GetAssemblyNameFromScriptPath(
                    assetPath
                );

            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                return false;
            }

            if (
                !assemblyName.EndsWith(
                    ".dll",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                assemblyName += ".dll";
            }

            string assemblyPath =
                Path.Combine(
                    projectRoot,
                    "Library",
                    "ScriptAssemblies",
                    assemblyName
                );

            if (!File.Exists(assemblyPath))
            {
                return false;
            }

            DateTime sourceWriteUtc =
                File.GetLastWriteTimeUtc(sourcePath);

            DateTime assemblyWriteUtc =
                File.GetLastWriteTimeUtc(assemblyPath);

            return assemblyWriteUtc >= sourceWriteUtc;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCompiledMonoBehaviourLoaded(
        string className
    )
    {
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

    private static void KickCompilation()
    {
        string assetPath =
            SessionState.GetString(JobAssetPathKey, "");

        if (string.IsNullOrWhiteSpace(assetPath))
        {
            return;
        }

        SessionState.SetString(
            WatchdogLastKickTicksKey,
            DateTime.UtcNow.Ticks.ToString()
        );

        try
        {
            AssetDatabase.ImportAsset(
                assetPath,
                ImportAssetOptions.ForceUpdate
            );

            CompilationPipeline.RequestScriptCompilation();
        }
        catch (Exception ex)
        {
            // Keep the original job alive. The persistent-script server owns
            // final failure classification; this watchdog is recovery only.
            SessionState.SetString(
                JobDiagnosticsKey,
                "Compile watchdog: "
                + ex.GetType().Name
                + ": "
                + ex.Message
            );
        }
    }

    private static void MarkCompiled()
    {
        SessionState.SetBool(
            JobCompilationStartedKey,
            true
        );

        SessionState.SetString(
            JobStateKey,
            "compiled"
        );

        SessionState.SetString(
            JobDiagnosticsKey,
            ""
        );
    }

    private static bool IsActiveState(string state)
    {
        return
            string.Equals(
                state,
                "pending",
                StringComparison.Ordinal
            )
            || string.Equals(
                state,
                "compiling",
                StringComparison.Ordinal
            );
    }

    private static double SecondsSinceWatchdogKick()
    {
        string ticksText =
            SessionState.GetString(
                WatchdogLastKickTicksKey,
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
            return Math.Max(
                0.0,
                (
                    DateTime.UtcNow
                    - new DateTime(
                        ticks,
                        DateTimeKind.Utc
                    )
                ).TotalSeconds
            );
        }
        catch
        {
            return double.MaxValue;
        }
    }
}
