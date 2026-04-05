Shader "Custom/ToonWater"
{
    // BotW-style water with REAL DEPTH from camera depth texture.
    // Shallow = turchese, Deep = blu scuro, Shore = schiuma bianca.
    // Opaque for performance, depth-based color blending.
    Properties
    {
        _ShallowColor ("Shallow Color", Color) = (0.15, 0.78, 0.82, 1)
        _DeepColor ("Deep Color", Color) = (0.02, 0.10, 0.48, 1)
        _FoamColor ("Foam Color", Color) = (0.92, 0.97, 1.00, 1)
        _HorizonColor ("Horizon Color", Color) = (0.45, 0.70, 0.95, 1)
        _ShadowColor ("Shadow Tint", Color) = (0.08, 0.06, 0.30, 1)

        [Header(Depth)]
        _ShallowDepth ("Shallow Depth (m)", Float) = 3.0
        _DeepDepth ("Deep Depth (m)", Float) = 25.0
        _ShoreWidth ("Shore Foam Width (m)", Float) = 2.0

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
        // Opaque, rendered right after terrain geometry
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
            // Depth texture per profondita' acqua
            TEXTURE2D_X_FLOAT(_CameraDepthTexture);
            SAMPLER(sampler_CameraDepthTexture);

            struct Attributes
            {
                float4 posOS : POSITION;
                float2 uv    : TEXCOORD0;
            };

            struct Varyings
            {
                float4 posCS    : SV_POSITION;
                float3 posWS    : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float  fogCoord : TEXCOORD2;
                float  waveH    : TEXCOORD3;
                float4 screenPos : TEXCOORD4;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float4 _FoamColor;
                float4 _HorizonColor;
                float4 _ShadowColor;
                float  _ShallowDepth, _DeepDepth, _ShoreWidth;
                float  _WaveSpeed, _WaveScale;
                float  _WaveFreq1, _WaveFreq2, _WaveFreq3;
                float  _WaveHeight;
                float4 _WaveDir1, _WaveDir2, _WaveDir3;
                float  _FoamScale, _FoamThreshold, _FoamSoft, _FoamSpeed;
                float  _Steps, _SoftEdge, _FresnelPower, _RimStrength;
                float  _SpecPower, _SpecStrength;
            CBUFFER_END

            float Wave(float3 pos, float2 dir, float freq, float speed, float t)
            {
                return sin(dot(dir, pos.xz) * freq - t * speed * freq);
            }
            float WaveDx(float3 pos, float2 dir, float freq, float speed, float t)
            {
                return cos(dot(dir, pos.xz) * freq - t * speed * freq) * freq * dir.x;
            }
            float WaveDz(float3 pos, float2 dir, float freq, float speed, float t)
            {
                return cos(dot(dir, pos.xz) * freq - t * speed * freq) * freq * dir.y;
            }

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

                float h = Wave(posWS, d1, _WaveFreq1, _WaveSpeed*4, t) * _WaveHeight
                        + Wave(posWS, d2, _WaveFreq2, _WaveSpeed*2.5, t) * _WaveHeight * 0.5
                        + Wave(posWS, d3, _WaveFreq3, _WaveSpeed*1.2, t) * _WaveHeight * 0.2;

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
                OUT.screenPos = OUT.posCS; // pass clip-space pos, compute UV in frag
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                Light mainLight = GetMainLight();
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.posWS));
                float3 L = mainLight.direction;

                // ── DEPTH-BASED COLOR ──
                // Sample depth texture to get terrain depth below water
                // Compute screen UV from clip-space position
                float2 screenUV = (IN.screenPos.xy / IN.screenPos.w) * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                screenUV.y = 1.0 - screenUV.y;
                #endif
                float depthDiff = _DeepDepth; // default: deep water

                #if defined(_MAIN_LIGHT_SHADOWS) || 1
                // Try to sample scene depth
                float sceneDepthRaw = SAMPLE_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, screenUV).r;
                float sceneDepthLinear = LinearEyeDepth(sceneDepthRaw, _ZBufferParams);
                float waterSurfaceDepth = IN.posCS.w; // linear depth of water surface
                depthDiff = max(0, sceneDepthLinear - waterSurfaceDepth);
                // Sanity check: if depth texture returns garbage, use wave-based fallback
                if (depthDiff > 500.0 || depthDiff < 0) depthDiff = _DeepDepth * 0.6;
                #endif

                // Clamp and normalize depth
                float shallowFactor = saturate(depthDiff / _ShallowDepth);
                float deepFactor = saturate(depthDiff / _DeepDepth);
                float shoreFactor = 1.0 - saturate(depthDiff / _ShoreWidth);

                // Soft-stepped diffuse
                float NdotL = dot(N, L);
                float halfLambert = NdotL * 0.5 + 0.5;
                float toon = SoftStep(halfLambert, _Steps, _SoftEdge);

                // Fresnel
                float fresnel = pow(1.0 - saturate(dot(V, N)), _FresnelPower);

                // Base color: blend shallow -> deep based on REAL depth
                float3 baseColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, deepFactor);
                // Horizon tint at distance via fresnel
                baseColor = lerp(baseColor, _HorizonColor.rgb, fresnel * 0.6);

                // Apply lighting with colored shadows
                float3 litColor = lerp(_ShadowColor.rgb * baseColor, baseColor, toon);

                // Specular
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _SpecPower);
                float toonSpec = SoftStep(spec, 2, 0.15);
                litColor += mainLight.color.rgb * toonSpec * _SpecStrength;

                // Rim highlight
                litColor += _HorizonColor.rgb * fresnel * _RimStrength;

                // Foam: stronger at shore + wave crests
                float t = _Time.y * _FoamSpeed;
                float2 fuv = IN.posWS.xz * _FoamScale * 0.01;
                float foam = vnoise(fuv + float2(t, t*0.7))
                           + vnoise(fuv * 2.3 - float2(t*0.5, t*0.3)) * 0.5;
                foam /= 1.5;

                float crestFactor = saturate(IN.waveH * 0.5 + 0.3);
                // Shore foam (where water meets land)
                float shoreFoam = shoreFactor * 0.7;
                float totalFoamDrive = max(crestFactor, shoreFoam);
                float foamMask = smoothstep(_FoamThreshold - _FoamSoft, _FoamThreshold + _FoamSoft, foam * totalFoamDrive);
                litColor = lerp(litColor, _FoamColor.rgb * (toon * 0.3 + 0.7), foamMask * 0.6);

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
