Shader "Custom/ToonWater"
{
    // BotW-style toon water. Opaque, no depth texture dependency.
    // Color varies with wave height + view angle + noise for depth illusion.
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.15, 0.78, 0.82, 1)
        _DeepColor ("Deep Color", Color) = (0.02, 0.10, 0.48, 1)
        _FoamColor ("Foam Color", Color) = (0.92, 0.97, 1.00, 1)
        _HorizonColor ("Horizon Color", Color) = (0.45, 0.70, 0.95, 1)
        _ShadowColor ("Shadow Tint", Color) = (0.08, 0.06, 0.30, 1)

        [Header(Waves)]
        _WaveSpeed ("Wave Speed", Float) = 1.0
        _WaveScale ("Wave Scale", Float) = 0.02
        _WaveFreq1 ("Wave Freq 1", Float) = 0.8
        _WaveFreq2 ("Wave Freq 2", Float) = 1.5
        _WaveFreq3 ("Wave Freq 3", Float) = 2.8
        _WaveHeight ("Wave Height", Float) = 0.8
        _WaveDir1 ("Wave Dir 1", Vector) = (-0.7, 0, -0.7, 0)
        _WaveDir2 ("Wave Dir 2", Vector) = (0, 0, -1, 0)
        _WaveDir3 ("Wave Dir 3", Vector) = (0.7, 0, -0.7, 0)

        [Header(Foam)]
        _FoamScale ("Foam Scale", Float) = 12.0
        _FoamThreshold ("Foam Threshold", Range(0, 1)) = 0.55
        _FoamSoft ("Foam Softness", Range(0, 0.3)) = 0.1
        _FoamSpeed ("Foam Speed", Float) = 0.3

        [Header(Look)]
        _Steps ("Color Steps", Range(2, 8)) = 3
        _SoftEdge ("Step Softness", Range(0, 0.3)) = 0.06
        _FresnelPower ("Fresnel Power", Float) = 2.5
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.25
        _SpecPower ("Specular Power", Float) = 60
        _SpecStrength ("Specular Strength", Range(0, 1)) = 0.55
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry+10" }
        LOD 200

        Pass
        {
            Name "BotWWater"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 posOS : POSITION;
            };

            struct Varyings
            {
                float4 posCS    : SV_POSITION;
                float3 posWS    : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float  fogCoord : TEXCOORD2;
                float  waveH    : TEXCOORD3;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor, _DeepColor, _FoamColor, _HorizonColor, _ShadowColor;
                float  _WaveSpeed, _WaveScale;
                float  _WaveFreq1, _WaveFreq2, _WaveFreq3, _WaveHeight;
                float4 _WaveDir1, _WaveDir2, _WaveDir3;
                float  _FoamScale, _FoamThreshold, _FoamSoft, _FoamSpeed;
                float  _Steps, _SoftEdge, _FresnelPower, _RimStrength;
                float  _SpecPower, _SpecStrength;
            CBUFFER_END

            // --- Utility ---
            float Wave(float3 p, float2 d, float f, float s, float t)
            { return sin(dot(d, p.xz) * f - t * s * f); }

            float WaveDx(float3 p, float2 d, float f, float s, float t)
            { return cos(dot(d, p.xz) * f - t * s * f) * f * d.x; }

            float WaveDz(float3 p, float2 d, float f, float s, float t)
            { return cos(dot(d, p.xz) * f - t * s * f) * f * d.y; }

            float hash2(float2 p) {
                float3 p3 = frac(float3(p.xyx) * 0.13);
                p3 += dot(p3, p3.yzx + 3.333);
                return frac((p3.x + p3.y) * p3.z);
            }
            float vnoise(float2 p) {
                float2 i = floor(p); float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash2(i), hash2(i+float2(1,0)), f.x),
                            lerp(hash2(i+float2(0,1)), hash2(i+float2(1,1)), f.x), f.y);
            }

            float SoftStep(float v, float steps, float edge)
            {
                float stepped = floor(v * steps) / steps;
                float next = ceil(v * steps) / steps;
                float f = frac(v * steps);
                float s = smoothstep(0.5 - edge * steps, 0.5 + edge * steps, f);
                return lerp(stepped, next, s);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.posOS.xyz);
                float t = _Time.y * _WaveSpeed;
                float2 d1 = normalize(_WaveDir1.xz);
                float2 d2 = normalize(_WaveDir2.xz);
                float2 d3 = normalize(_WaveDir3.xz);

                float h = Wave(posWS,d1,_WaveFreq1,_WaveSpeed*4,t) * _WaveHeight
                        + Wave(posWS,d2,_WaveFreq2,_WaveSpeed*2.5,t) * _WaveHeight * 0.5
                        + Wave(posWS,d3,_WaveFreq3,_WaveSpeed*1.2,t) * _WaveHeight * 0.2;
                posWS.y += h * _WaveScale;

                float dx = WaveDx(posWS,d1,_WaveFreq1,_WaveSpeed*4,t)*_WaveHeight*_WaveScale
                         + WaveDx(posWS,d2,_WaveFreq2,_WaveSpeed*2.5,t)*_WaveHeight*0.5*_WaveScale
                         + WaveDx(posWS,d3,_WaveFreq3,_WaveSpeed*1.2,t)*_WaveHeight*0.2*_WaveScale;
                float dz = WaveDz(posWS,d1,_WaveFreq1,_WaveSpeed*4,t)*_WaveHeight*_WaveScale
                         + WaveDz(posWS,d2,_WaveFreq2,_WaveSpeed*2.5,t)*_WaveHeight*0.5*_WaveScale
                         + WaveDz(posWS,d3,_WaveFreq3,_WaveSpeed*1.2,t)*_WaveHeight*0.2*_WaveScale;

                OUT.posWS = posWS;
                OUT.normalWS = normalize(float3(-dx, 1, -dz));
                OUT.posCS = TransformWorldToHClip(posWS);
                OUT.fogCoord = ComputeFogFactor(OUT.posCS.z);
                OUT.waveH = h;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                Light mainLight = GetMainLight();
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.posWS));
                float3 L = mainLight.direction;

                // Toon lighting
                float halfLambert = dot(N, L) * 0.5 + 0.5;
                float toon = SoftStep(halfLambert, _Steps, _SoftEdge);

                // Fresnel (view-dependent depth illusion)
                float fresnel = pow(1.0 - saturate(dot(V, N)), _FresnelPower);

                // Depth illusion from noise + wave height
                // Gives spatial variation: some areas look shallow, some deep
                float t = _Time.y * 0.05;
                float spatialNoise = vnoise(IN.posWS.xz * 0.003 + float2(t, t*0.7));
                float depthFactor = saturate(spatialNoise * 0.6 + fresnel * 0.4);
                // Wave crests = shallow, troughs = deep
                depthFactor = saturate(depthFactor - IN.waveH * 0.15);

                // Base color: shallow -> deep
                float3 baseColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthFactor);
                // Horizon blend
                baseColor = lerp(baseColor, _HorizonColor.rgb, fresnel * 0.6);

                // Lit with colored shadows
                float3 litColor = lerp(_ShadowColor.rgb * baseColor, baseColor, toon);

                // Specular
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _SpecPower);
                float toonSpec = SoftStep(spec, 2, 0.15);
                litColor += mainLight.color.rgb * toonSpec * _SpecStrength;

                // Rim
                litColor += _HorizonColor.rgb * fresnel * _RimStrength;

                // Foam (wave crests + noise)
                float ft = _Time.y * _FoamSpeed;
                float2 fuv = IN.posWS.xz * _FoamScale * 0.01;
                float foam = vnoise(fuv + float2(ft, ft*0.7))
                           + vnoise(fuv * 2.3 - float2(ft*0.5, ft*0.3)) * 0.5;
                foam /= 1.5;
                float crestFactor = saturate(IN.waveH * 0.5 + 0.3);
                float foamMask = smoothstep(_FoamThreshold - _FoamSoft,
                                            _FoamThreshold + _FoamSoft, foam * crestFactor);
                litColor = lerp(litColor, _FoamColor.rgb * (toon * 0.3 + 0.7), foamMask * 0.5);

                litColor = MixFog(litColor, IN.fogCoord);
                return half4(litColor, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex SV
            #pragma fragment SF
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 p : POSITION; };
            struct V { float4 p : SV_POSITION; };
            V SV(A i) { V o; o.p = TransformObjectToHClip(i.p.xyz); return o; }
            half4 SF(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}
