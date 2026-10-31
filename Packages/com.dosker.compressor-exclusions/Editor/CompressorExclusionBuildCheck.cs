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
            if (CompressorExclusionPatch.SolverError == null) return true;
            if (!CompressorExclusionSettings.Instance.active) return true;

            Debug.LogError($"[Compressor Exclusions] {CompressorExclusionPatch.SolverError}");
            return EditorUtility.DisplayDialog(
                "Compressor Exclusions",
                "O patch não está ativo, então o VRCFury vai comprimir os parâmetros sem respeitar suas proteções.\n\n"
                + CompressorExclusionPatch.SolverError,
                "Continuar mesmo assim",
                "Cancelar o build"
            );
        }
    }
}
