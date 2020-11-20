using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    unsafe struct ShaderVariablesClouds
    {
        public float _CloudDomeSize;
        public float _HighestCloudAltitude;
        public float _LowestCloudAltitude;
        public int _NumPrimarySteps;

        public int _NumLightSteps;
        public Vector3 _ScatteringTint;

        public float _EccentricityF;
        public float _EccentricityB;
        public float _PhaseFunctionBlend;
        public float _PowderEffectIntensity;

        public int _ExposureSunColor;
        public Vector3 _SunLightColor;
        public Vector3 _SunDirection;
        public int _AccumulationFrameIndex;

        public Vector3 _WindDirection;
        public float _MultiScattering;

        public float _DensityMultiplier;
        public Vector3 _Padding;

        [HLSLArray(7, typeof(Vector4))]
        public fixed float _AmbientProbeCoeffs[7 * 4];  // 3 bands of SH, packed, rescaled and convolved with the phase function
    }

    public partial class HDRenderPipeline
    {
        ShaderVariablesClouds m_ShaderVariablesCloudsCB = new ShaderVariablesClouds();
        Vector4[] m_PackedCoeffsClouds;
        ZonalHarmonicsL2 m_PhaseZHClouds;
        int m_CloudRenderKernel;
        int m_CloudCombineKernel;

        void InitializeVolumetricClouds()
        {
            m_PackedCoeffsClouds = new Vector4[7];
            m_PhaseZHClouds = new ZonalHarmonicsL2();
            m_PhaseZHClouds.coeffs = new float[3];

            // Grab the kernels we need
            ComputeShader volumetricCloudsCS = m_Asset.renderPipelineResources.shaders.volumetricCloudsCS;
            m_CloudRenderKernel = volumetricCloudsCS.FindKernel("RenderClouds");
            m_CloudCombineKernel = volumetricCloudsCS.FindKernel("CombineClouds");
        }

        unsafe void SetPreconvolvedAmbientLightProbe(ref ShaderVariablesClouds cb, HDCamera hdCamera, VolumetricClouds settings)
        {
            SphericalHarmonicsL2 probeSH = SphericalHarmonicMath.UndoCosineRescaling(m_SkyManager.GetAmbientProbe(hdCamera));
            probeSH = SphericalHarmonicMath.RescaleCoefficients(probeSH, settings.globalLightProbeDimmer.value);
            ZonalHarmonicsL2.GetCornetteShanksPhaseFunction(m_PhaseZHClouds, 0.0f);
            SphericalHarmonicsL2 finalSH = SphericalHarmonicMath.PremultiplyCoefficients(SphericalHarmonicMath.Convolve(probeSH, m_PhaseZHClouds));

            SphericalHarmonicMath.PackCoefficients(m_PackedCoeffsClouds, finalSH);
            for (int i = 0; i < 7; i++)
                for (int j = 0; j < 4; ++j)
                    cb._AmbientProbeCoeffs[i * 4 + j] = m_PackedCoeffsClouds[i][j];
        }

        static RTHandle VolumetricCloudsHistoryBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
        {
            return rtHandleSystem.Alloc(Vector2.one, TextureXR.slices, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, dimension: TextureXR.dimension,
                                        enableRandomWrite: true, useMipMap: false, autoGenerateMips: false,
                                        name: string.Format("{0}_CloudsHistoryBuffer{1}", viewName, frameIndex));
        }

        static RTHandle RequestVolumetricCloudsHistoryTexture(HDCamera hdCamera)
        {
            return hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.VolumetricClouds)
                ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.VolumetricClouds,
                VolumetricCloudsHistoryBufferAllocatorFunction, 1);
        }

        void UpdateShaderVariableslClouds(ref ShaderVariablesClouds cb, HDCamera hdCamera, VolumetricClouds settings)
        {
            // Convert to kilometers
            cb._CloudDomeSize = settings.cloudDomeSize.value * 1000.0f;
            cb._LowestCloudAltitude = settings.lowestCloudAltitude.value;
            cb._HighestCloudAltitude = settings.highestCloudAltitude.value;
            cb._NumPrimarySteps = settings.numPrimarySteps.value;
            cb._NumLightSteps = settings.numLightSteps.value;
            cb._ScatteringTint = new Vector3(settings.scatteringTint.value.r, settings.scatteringTint.value.g, settings.scatteringTint.value.b);
            cb._EccentricityF = settings.eccentricityF.value;
            cb._EccentricityB = settings.eccentricityB.value;
            cb._PhaseFunctionBlend = settings.phaseFunctionBlend.value;
            cb._PowderEffectIntensity = settings.powderEffectIntensity.value;
            cb._AccumulationFrameIndex = RayTracingFrameIndex(hdCamera, 16);
            if (m_lightList.directionalLights.Count != 0)
            {
                cb._SunDirection = -m_lightList.directionalLights[0].forward;
                cb._SunLightColor = m_lightList.directionalLights[0].color;
                cb._ExposureSunColor = 1;
            }   
            else
            {
                cb._SunDirection = Vector3.up;
                cb._SunLightColor = Vector3.one;
                cb._ExposureSunColor = 0;
            }
            cb._AccumulationFrameIndex = RayTracingFrameIndex(hdCamera);

            // Evaluate the ambient probe data
            SetPreconvolvedAmbientLightProbe(ref cb, hdCamera, settings);

            float theta = settings.windRotation.value * Mathf.PI * 2.0f;
            cb._WindDirection = new Vector3(Mathf.Cos(theta), Mathf.Sin(theta), 0.0f);
            cb._MultiScattering = 1.0f - settings.multiScattering.value * 0.8f;
            cb._DensityMultiplier = settings.densityMultiplier.value;
        }

        struct VolumetricCloudsParameters
        {
            // Camera parameters
            public int texWidth;
            public int texHeight;
            public int viewCount;

            // Other data
            public Texture3D worley128RGBA;
            public Texture3D worley32RGB;
            public Texture cloudMapTexture;
            public Texture cloudLutTexture;
            public ComputeShader volumetricCloudsCS;
            public int renderKernel;
            public int combineKernel;
            public BlueNoise.DitheredTextureSet ditheredTextureSet;
            public ShaderVariablesClouds cloudsCB;
        }

        VolumetricCloudsParameters PrepareVolumetricCloudsParameters(HDCamera hdCamera)
        {
            VolumetricCloudsParameters parameters = new VolumetricCloudsParameters();
            // Camera parameters
            parameters.texWidth = hdCamera.actualWidth;
            parameters.texHeight = hdCamera.actualHeight;
            parameters.viewCount = hdCamera.viewCount;

            // Grab the volume component
            VolumetricClouds settings = hdCamera.volumeStack.GetComponent<VolumetricClouds>();

            // Update the constant buffer
            UpdateShaderVariableslClouds(ref m_ShaderVariablesCloudsCB, hdCamera, settings);
            parameters.cloudsCB = m_ShaderVariablesCloudsCB;

            parameters.volumetricCloudsCS = m_Asset.renderPipelineResources.shaders.volumetricCloudsCS;
            parameters.renderKernel = m_CloudRenderKernel;
            parameters.combineKernel = m_CloudCombineKernel;
            parameters.cloudMapTexture = settings.cloudMap.value;
            parameters.cloudLutTexture = settings.cloudLut.value;
            parameters.worley128RGBA = m_Asset.renderPipelineResources.textures.worleyNoise128RGBA;
            parameters.worley32RGB = m_Asset.renderPipelineResources.textures.worleyNoise32RGB;
            BlueNoise blueNoise = GetBlueNoiseManager();
            parameters.ditheredTextureSet = blueNoise.DitheredTextureSet8SPP();

            return parameters;
        }

        static void TraceVolumetricClouds(CommandBuffer cmd, VolumetricCloudsParameters parameters, RTHandle colorBuffer, RTHandle depthPyramid, TextureHandle motionVectors, RTHandle historyBuffer, RTHandle intermediateBuffer0, RTHandle intermediateBuffer1)
        {
            BlueNoise.BindDitheredTextureSet(cmd, parameters.ditheredTextureSet);

            // Bind all the input data
            ConstantBuffer.Push(cmd, parameters.cloudsCB, parameters.volumetricCloudsCS, HDShaderIDs._ShaderVariablesClouds);

            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._CameraColorTexture, colorBuffer);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._DepthTexture, depthPyramid);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._Worley128RGBA, parameters.worley128RGBA);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._Worley32RGB, parameters.worley32RGB);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._CloudMapTexture, parameters.cloudMapTexture);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._CloudLutTexture, parameters.cloudLutTexture);

            // Bind the output buffers
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.renderKernel, HDShaderIDs._VolumetricCloudsTextureRW, intermediateBuffer0);

            // Evaluate the dispatch parameters
            int numTilesXHR = (parameters.texWidth + (8 - 1)) / 8;
            int numTilesYHR = (parameters.texHeight + (8 - 1)) / 8;
            cmd.DispatchCompute(parameters.volumetricCloudsCS, parameters.renderKernel, numTilesXHR, numTilesYHR, parameters.viewCount);

            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.combineKernel, HDShaderIDs._CameraMotionVectorsTexture, motionVectors);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.combineKernel, HDShaderIDs._VolumetricCloudsTexture, intermediateBuffer0);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.combineKernel, HDShaderIDs._HistoryVolumetricCloudsTexture, historyBuffer);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.combineKernel, HDShaderIDs._HistoryVolumetricCloudsTextureRW, intermediateBuffer1);
            cmd.SetComputeTextureParam(parameters.volumetricCloudsCS, parameters.combineKernel, HDShaderIDs._CameraColorTextureRW, colorBuffer);
            cmd.DispatchCompute(parameters.volumetricCloudsCS, parameters.combineKernel, numTilesXHR, numTilesYHR, parameters.viewCount);

            // Propagate the history buffer result to the history buffer for the next frame
            HDUtils.BlitCameraTexture(cmd, intermediateBuffer1, historyBuffer);
        }

        class VolumetricCloudsData
        {
            public VolumetricCloudsParameters parameters;
            public TextureHandle colorBuffer;
            public TextureHandle depthPyramid;
            public TextureHandle motionVectors;
            public TextureHandle historyBuffer;
            public TextureHandle intermediateBuffer0;
            public TextureHandle intermediateBuffer1;
        }

        TextureHandle TraceVolumetricClouds(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle colorBuffer, TextureHandle depthPyramid, TextureHandle motionVectors)
        {
            using (var builder = renderGraph.AddRenderPass<VolumetricCloudsData>("Generating the rays for RTR", out var passData, ProfilingSampler.Get(HDProfileId.RaytracingIndirectDiffuseDirectionGeneration)))
            {
                builder.EnableAsyncCompute(false);

                passData.parameters = PrepareVolumetricCloudsParameters(hdCamera);
                passData.colorBuffer = builder.ReadTexture(builder.WriteTexture(colorBuffer));
                passData.depthPyramid = builder.ReadTexture(depthPyramid);
                passData.motionVectors = builder.ReadTexture(motionVectors);
                passData.historyBuffer = renderGraph.ImportTexture(RequestVolumetricCloudsHistoryTexture(hdCamera));
                passData.intermediateBuffer0 = builder.CreateTransientTexture(new TextureDesc(Vector2.one, true, true)
                { colorFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "Temporary Clouds Buffer0" });
                passData.intermediateBuffer1 = builder.CreateTransientTexture(new TextureDesc(Vector2.one, true, true)
                { colorFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "Temporary Clouds Buffer1" });

                builder.SetRenderFunc(
                (VolumetricCloudsData data, RenderGraphContext ctx) =>
                {
                    TraceVolumetricClouds(ctx.cmd, data.parameters, data.colorBuffer, data.depthPyramid, data.motionVectors, data.historyBuffer, data.intermediateBuffer0, data.intermediateBuffer1);
                });

                return passData.colorBuffer;
            }
        }

        void RenderVolumetricClouds(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle colorBuffer, TextureHandle depthPyramid, TextureHandle motionVector)
        {
            VolumetricClouds settings = hdCamera.volumeStack.GetComponent<VolumetricClouds>();
            // If the current volume does not enable the feature, quit right away.
            if (!settings.enable.value)
                return;

            TraceVolumetricClouds(renderGraph, hdCamera, colorBuffer, depthPyramid, motionVector);
        }
    }
}
