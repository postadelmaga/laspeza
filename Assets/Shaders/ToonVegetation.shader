Shader "Custom/ToonVegetation"
{
    // BotW-style vegetation: soft cel-shading, colored shadows, rim light, wind.
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.18, 0.62, 0.12, 1)
        _ShadowColor ("Shadow Color", Color) = (0.10, 0.12, 0.28, 1)
        _RimColor ("Rim Color", Color) = (0.50, 0.75, 0.25, 1)
        _Steps ("Light Steps", Range(2, 8)) = 2
        _SoftEdge ("Soft Edge", Range(0, 0.3)) = 0.05
        _RimPower ("Rim Power", Float) = 2.5
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.45
        _WindSpeed ("Wind Speed", Float) = 1.2
        _WindStrength ("Wind Strength", Float) = 0.20
        _WindFreq ("Wind Frequency", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "BotWVeg"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 posOS    : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 posCS    : SV_POSITION;
                float3 posWS    : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float  fogCoord : TEXCOORD2;
                float  height01 : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _ShadowColor;
                float4 _RimColor;
                float  _Steps;
                float  _SoftEdge;
                float  _RimPower;
                float  _RimStrength;
                float  _WindSpeed;
                float  _WindStrength;
                float  _WindFreq;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 posWS = TransformObjectToWorld(IN.posOS.xyz);

                // Wind: stronger at top
                float heightFactor = saturate(IN.posOS.y * 0.25);
                float windPhase = _Time.y * _WindSpeed + posWS.x * _WindFreq * 0.1 + posWS.z * _WindFreq * 0.07;
                float windMain = sin(windPhase) * _WindStrength * heightFactor;
                float windCross = sin(windPhase * 1.3 + 1.7) * _WindStrength * 0.3 * heightFactor;
                // Subtle flutter (high freq)
                float flutter = sin(windPhase * 4.1 + posWS.y * 2.0) * _WindStrength * 0.08 * heightFactor;
                posWS.x += windMain + flutter;
                posWS.z += windCross;

                OUT.posWS = posWS;
                OUT.posCS = TransformWorldToHClip(posWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.fogCoord = ComputeFogFactor(OUT.posCS.z);
                OUT.height01 = heightFactor;

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                Light mainLight = GetMainLight();
                float3 normal = normalize(IN.normalWS);
                float3 viewDir = normalize(GetWorldSpaceViewDir(IN.posWS));

                // BotW-style soft stepped lighting
                float NdotL = dot(normal, mainLight.direction);
                float halfLambert = NdotL * 0.5 + 0.5;

                // Soft quantize: smoothstep at each step boundary
                float stepped = floor(halfLambert * _Steps) / _Steps;
                float nextStep = ceil(halfLambert * _Steps) / _Steps;
                float frac_val = frac(halfLambert * _Steps);
                float softFactor = smoothstep(0.5 - _SoftEdge * _Steps, 0.5 + _SoftEdge * _Steps, frac_val);
                float toon = lerp(stepped, nextStep, softFactor);

                // Color: lerp between shadow color and base (colored shadows!)
                float3 col = lerp(_ShadowColor.rgb, _BaseColor.rgb, toon);

                // Height variation: lighter tips (subsurface scatter approx)
                col += IN.height01 * float3(0.03, 0.06, 0.02);

                // Rim light (BotW has subtle edge glow on vegetation)
                float rim = pow(1.0 - saturate(dot(viewDir, normal)), _RimPower);
                col += _RimColor.rgb * rim * _RimStrength;

                // Apply light color
                col *= lerp(float3(1,1,1), mainLight.color.rgb, 0.3);

                col = MixFog(col, IN.fogCoord);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 posOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 posCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            Varyings ShadowVert(Attributes IN) {
                Varyings OUT; UNITY_SETUP_INSTANCE_ID(IN); UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.posCS = TransformObjectToHClip(IN.posOS.xyz); return OUT;
            }
            half4 ShadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
