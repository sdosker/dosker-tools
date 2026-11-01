using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Category = VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory;

namespace DoskerTools.PerformanceBudget
{
    // Mede com o próprio SDK. As notas também vêm dele, escrevendo os valores nas estatísticas,
    // para não existir aqui uma cópia dos limites do ranking.
    internal static class BudgetProbe
    {
        private sealed class Field
        {
            public string Category;
            public Func<AvatarPerformanceStats, float?> Read;
            public Action<AvatarPerformanceStats, float> Write;
        }

        private static readonly Field[] Fields = new[]
        {
            Int(Category.PolyCount, s => s.polyCount, (s, v) => s.polyCount = v),
            Int(Category.SkinnedMeshCount, s => s.skinnedMeshCount, (s, v) => s.skinnedMeshCount = v),
            Int(Category.MeshCount, s => s.meshCount, (s, v) => s.meshCount = v),
            Int(Category.MaterialCount, s => s.materialCount, (s, v) => s.materialCount = v),
            Int(Category.BoneCount, s => s.boneCount, (s, v) => s.boneCount = v),
            Int(Category.AnimatorCount, s => s.animatorCount, (s, v) => s.animatorCount = v),
            PhysBone(Category.PhysBoneComponentCount, p => p.componentCount, (ref AvatarPerformanceStats.PhysBoneStats p, int v) => p.componentCount = v),
            PhysBone(Category.PhysBoneTransformCount, p => p.transformCount, (ref AvatarPerformanceStats.PhysBoneStats p, int v) => p.transformCount = v),
            PhysBone(Category.PhysBoneColliderCount, p => p.colliderCount, (ref AvatarPerformanceStats.PhysBoneStats p, int v) => p.colliderCount = v),
            PhysBone(Category.PhysBoneCollisionCheckCount, p => p.collisionCheckCount, (ref AvatarPerformanceStats.PhysBoneStats p, int v) => p.collisionCheckCount = v),
            Int(Category.ContactCount, s => s.contactCount, (s, v) => s.contactCount = v),
            Int(Category.ConstraintsCount, s => s.constraintsCount, (s, v) => s.constraintsCount = v),
            Int(Category.ConstraintDepth, s => s.constraintDepth, (s, v) => s.constraintDepth = v),
            Int(Category.LightCount, s => s.lightCount, (s, v) => s.lightCount = v),
            Int(Category.ParticleSystemCount, s => s.particleSystemCount, (s, v) => s.particleSystemCount = v),
            Int(Category.ParticleTotalCount, s => s.particleTotalCount, (s, v) => s.particleTotalCount = v),
            Int(Category.ParticleMaxMeshPolyCount, s => s.particleMaxMeshPolyCount, (s, v) => s.particleMaxMeshPolyCount = v),
            Flag(Category.ParticleTrailsEnabled, s => s.particleTrailsEnabled, (s, v) => s.particleTrailsEnabled = v),
            Flag(Category.ParticleCollisionEnabled, s => s.particleCollisionEnabled, (s, v) => s.particleCollisionEnabled = v),
            Int(Category.TrailRendererCount, s => s.trailRendererCount, (s, v) => s.trailRendererCount = v),
            Int(Category.LineRendererCount, s => s.lineRendererCount, (s, v) => s.lineRendererCount = v),
            Int(Category.ClothCount, s => s.clothCount, (s, v) => s.clothCount = v),
            Int(Category.ClothMaxVertices, s => s.clothMaxVertices, (s, v) => s.clothMaxVertices = v),
            Int(Category.PhysicsColliderCount, s => s.physicsColliderCount, (s, v) => s.physicsColliderCount = v),
            Int(Category.PhysicsRigidbodyCount, s => s.physicsRigidbodyCount, (s, v) => s.physicsRigidbodyCount = v),
            Int(Category.AudioSourceCount, s => s.audioSourceCount, (s, v) => s.audioSourceCount = v),
            Optional("RaycastCount", "raycastCount"),
            Optional("ForwardedParametersCount", "forwardedParametersCount"),
            Optional("FaceTrackingOperations", "faceTrackingOperations"),
            new Field { Category = Category.TextureMegabytes.ToString(), Read = s => s.textureMegabytes, Write = (s, v) => s.textureMegabytes = v },
        }.Where(field => field != null).ToArray();

        public static IEnumerable<string> Categories
        {
            get { return Fields.Select(field => field.Category); }
        }

        private static Field Int(Category category, Func<AvatarPerformanceStats, int?> read, Action<AvatarPerformanceStats, int> write)
        {
            return new Field { Category = category.ToString(), Read = s => read(s), Write = (s, v) => write(s, Mathf.RoundToInt(v)) };
        }

        // Só existem em versões recentes do SDK. Uma referência direta impediria a compilação nas antigas.
        private static Field Optional(string category, string fieldName)
        {
            var field = typeof(AvatarPerformanceStats).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null || !Enum.IsDefined(typeof(Category), category))
            {
                return null;
            }

            return new Field
            {
                Category = category,
                Read = s => field.GetValue(s) is int value ? value : (float?)null,
                Write = (s, v) => field.SetValue(s, (int?)Mathf.RoundToInt(v))
            };
        }

        private static Field Flag(Category category, Func<AvatarPerformanceStats, bool?> read, Action<AvatarPerformanceStats, bool> write)
        {
            return new Field
            {
                Category = category.ToString(),
                Read = s => read(s) == true ? 1f : 0f,
                Write = (s, v) => write(s, v > 0.5f)
            };
        }

        private delegate void PhysBoneWrite(ref AvatarPerformanceStats.PhysBoneStats stats, int value);

        private static Field PhysBone(Category category, Func<AvatarPerformanceStats.PhysBoneStats, int> read, PhysBoneWrite write)
        {
            return new Field
            {
                Category = category.ToString(),
                Read = s => s.physBone.HasValue ? read(s.physBone.Value) : (float?)null,
                Write = (s, v) =>
                {
                    var stats = s.physBone ?? default;
                    write(ref stats, Mathf.RoundToInt(v));
                    s.physBone = stats;
                }
            };
        }

        public static BudgetMeasurement Measure(GameObject avatar, bool mobile)
        {
            var stats = new AvatarPerformanceStats(mobile);
            AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, mobile);

            var output = new BudgetMeasurement();
            foreach (var field in Fields)
            {
                output.Set(field.Category, field.Read(stats) ?? 0f);
            }
            return output;
        }

        public static AvatarPerformanceStats Rate(BudgetMeasurement measurement, bool mobile)
        {
            var stats = new AvatarPerformanceStats(mobile);
            foreach (var field in Fields)
            {
                field.Write(stats, measurement.Get(field.Category));
            }
            stats.CalculateAllPerformanceRatings(mobile);
            return stats;
        }

        public static PerformanceRating RatingOf(AvatarPerformanceStats stats, string category)
        {
            return Enum.TryParse(category, out Category parsed) ? stats.GetPerformanceRatingForCategory(parsed) : PerformanceRating.None;
        }

        // Estrutura dos componentes que geram conteúdo no build. Mudanças dentro deles não aparecem aqui.
        public static string ComponentsFingerprint(GameObject avatar)
        {
            var lines = avatar.GetComponentsInChildren<Component>(true)
                .Where(component => component != null && IsBuildTimeComponent(component.GetType()))
                .Select(component => component.GetType().FullName + "@" + RelativePath(avatar.transform, component.transform))
                .OrderBy(line => line, StringComparer.Ordinal);
            return Hash128.Compute(string.Join("\n", lines)).ToString();
        }

        private static bool IsBuildTimeComponent(Type type)
        {
            return type.FullName != null && type.FullName.StartsWith("VF.", StringComparison.Ordinal);
        }

        private static string RelativePath(Transform root, Transform target)
        {
            var path = new StringBuilder(target.name);
            for (var current = target.parent; current != null && target != root && current != root; current = current.parent)
            {
                path.Insert(0, current.name + "/");
            }
            return path.ToString();
        }

        // Os renderers mais pesados, para saber por onde começar.
        public static List<(string name, int triangles)> HeaviestMeshes(GameObject avatar, int count)
        {
            var output = new List<(string name, int triangles)>();
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null)
                {
                    output.Add((renderer.name, TriangleCount(renderer.sharedMesh)));
                }
            }
            foreach (var filter in avatar.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                {
                    output.Add((filter.name, TriangleCount(filter.sharedMesh)));
                }
            }
            return output.OrderByDescending(mesh => mesh.triangles).Take(count).ToList();
        }

        private static int TriangleCount(Mesh mesh)
        {
            var total = 0L;
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                total += mesh.GetIndexCount(submesh) / 3;
            }
            return (int)total;
        }
    }
}
