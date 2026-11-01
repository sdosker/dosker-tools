using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace CompressorExclusions {
    /**
     * Runs right before VRCFury's ParameterCompressorHook (int.MaxValue - 100). If an update broke the patch,
     * the avatar would be compressed without the protections, so that must never happen silently.
     */
    public class CompressorExclusionBuildCheck : IVRCSDKPreprocessAvatarCallback {
        public int callbackOrder => int.MaxValue - 101;

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            var settings = CompressorExclusionSettings.Instance;
            // If the asset exists but isn't loaded, these are defaults and not the user's rules
            var settingsLoaded = EditorUtility.IsPersistent(settings);
            var error = settingsLoaded
                ? CompressorExclusionPatch.SolverError
                : "A configuração ainda não foi carregada. Tente o build de novo em alguns segundos.";
            if (error == null) return true;
            if (settingsLoaded && !settings.active) return true;

            Debug.LogError($"[Compressor Exclusions] {error}");
            // Nobody can answer a dialog in batch mode, so continuing here would lose the protections silently
            if (Application.isBatchMode) return false;

            return EditorUtility.DisplayDialog(
                "Compressor Exclusions",
                "O patch não está ativo, então o VRCFury vai comprimir os parâmetros sem respeitar suas proteções.\n\n"
                + error,
                "Continuar mesmo assim",
                "Cancelar o build"
            );
        }
    }
}
