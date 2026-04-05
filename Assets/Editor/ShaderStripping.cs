using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Stripping delle varianti shader inutili per il demo BotW.
/// Approccio conservativo: strippa solo ciò che è sicuramente inutile.
/// </summary>
public class ShaderStripping : IPreprocessShaders
{
    public int callbackOrder => -100;

    // Keyword sicuramente inutili
    static readonly HashSet<string> StripKeywords = new HashSet<string>
    {
        // Debug
        "DEBUG_DISPLAY",
        "_DEBUG_LIGHTING",
        "DEBUG_OUTPUT",

        // VR / XR (non usiamo VR)
        "STEREO_INSTANCING_ON",
        "UNITY_SINGLE_PASS_STEREO",
        "STEREO_MULTIVIEW_ON",
        "UNITY_STEREO_INSTANCING_ENABLED",
        "UNITY_STEREO_MULTIVIEW_ENABLED",

        // Texture detail maps (non usiamo)
        "_DETAIL_MULX2",
        "_DETAIL_SCALED",
        "_DETAIL_MAP",

        // PBR maps avanzate (non usiamo texture)
        "_METALLICSPECGLOSSMAP",
        "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A",
        "_OCCLUSIONMAP",
        "_PARALLAXMAP",
        "_SPECULAR_SETUP",

        // Lightmap (non usiamo baked lighting)
        "LIGHTMAP_SHADOW_MIXING",
        "SHADOWS_SHADOWMASK",
        "_MIXED_LIGHTING_SUBTRACTIVE",
        "DYNAMICLIGHTMAP_ON",
        "LIGHTMAP_ON",
        "DIRLIGHTMAP_COMBINED",

        // Decal (non usiamo)
        "_DBUFFER_MRT1",
        "_DBUFFER_MRT2",
        "_DBUFFER_MRT3",
        "DECALS_3RT",
        "DECALS_4RT",
        "DECAL_SURFACE_GRADIENT",

        // Deferred (usiamo forward)
        "_DEFERRED_STENCIL",
        "_DEFERRED_FIRST_LIGHT",
        "_DEFERRED_MAIN_LIGHT",
        "_GBUFFER_NORMALS_OCT",

        // HDR output (non serve)
        "_HDR_OUTPUT",
    };

    // Shader interi da strippare
    static readonly HashSet<string> StripShaderNames = new HashSet<string>
    {
        // Terrain detail che non usiamo (grass billboard)
        "Hidden/TerrainEngine/Details/UniversalPipeline/BillboardWavingDoublePass",
        "Hidden/TerrainEngine/Details/UniversalPipeline/WavingDoublePass",

        // Debug
        "Hidden/Universal Render Pipeline/Debug/DebugReplacement",
        "Hidden/Universal/HDRDebugView",

        // 2D (non usiamo sprite)
        "Universal Render Pipeline/2D/Sprite-Lit-Default",
        "Universal Render Pipeline/2D/Sprite-Unlit-Default",

        // Deferred shaders (usiamo forward)
        "Hidden/Universal Render Pipeline/StencilDeferred",
    };

    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        string shaderName = shader.name;

        // Strip shader interi
        if (StripShaderNames.Contains(shaderName))
        {
            data.Clear();
            return;
        }

        // Strip varianti con keyword inutili
        for (int i = data.Count - 1; i >= 0; i--)
        {
            var keywords = data[i].shaderKeywordSet.GetShaderKeywords();
            bool strip = false;

            foreach (var kw in keywords)
            {
                if (StripKeywords.Contains(kw.name))
                {
                    strip = true;
                    break;
                }
            }

            if (strip)
                data.RemoveAt(i);
        }
    }
}
