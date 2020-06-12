using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SharpVk;
using SharpVk.Glfw;
using SharpVk.Khronos;
using SharpVk.Multivendor;

namespace StarSimLib
{
    public class VkRenderer
    {
        private const int SurfaceHeight = 600;
        private const int SurfaceWidth = 800;
        private static readonly DebugReportCallbackDelegate DebugReportDelegate = DebugReport;

        private CommandBuffer[] commandBuffers;
        private CommandPool commandPool;
        private Device device;
        private ShaderModule fragShader;
        private Framebuffer[] frameBuffers;
        private Queue graphicsQueue;
        private Semaphore imageAvailableSemaphore;
        private Instance instance;
        private PhysicalDevice physicalDevice;
        private Pipeline pipeline;
        private PipelineLayout pipelineLayout;
        private Queue presentQueue;
        private Semaphore renderFinishedSemaphore;
        private RenderPass renderPass;
        private Surface surface;
        private Swapchain swapChain;
        private Extent2D swapChainExtent;
        private Format swapChainFormat;
        private Image[] swapChainImages;
        private ImageView[] swapChainImageViews;
        private ShaderModule vertShader;
        private WindowHandle window;
        private WindowSizeDelegate windowSizeCallback;

        private static Bool32 DebugReport(DebugReportFlags flags, DebugReportObjectType objectType, ulong @object, HostSize location, int messageCode, string layerPrefix, string message, IntPtr userData)
        {
            Console.WriteLine(message);

            return false;
        }

        private static uint[] LoadShaderData(string filePath, out int codeSize)
        {
            byte[] fileBytes = File.ReadAllBytes(filePath);
            uint[] shaderData = new uint[(int) Math.Ceiling(fileBytes.Length / 4f)];

            System.Buffer.BlockCopy(fileBytes, 0, shaderData, 0, fileBytes.Length);

            codeSize = fileBytes.Length;

            return shaderData;
        }

        private PresentMode ChooseSwapPresentMode(PresentMode[] availablePresentModes)
        {
            return availablePresentModes.Contains(PresentMode.Mailbox)
                    ? PresentMode.Mailbox
                    : PresentMode.Fifo;
        }

        private SurfaceFormat ChooseSwapSurfaceFormat(SurfaceFormat[] availableFormats)
        {
            if (availableFormats.Length == 1 && availableFormats[0].Format == Format.Undefined)
            {
                return new SurfaceFormat
                {
                    Format = Format.B8G8R8A8UNorm,
                    ColorSpace = ColorSpace.SrgbNonlinear
                };
            }

            foreach (SurfaceFormat format in availableFormats)
            {
                if (format.Format == Format.B8G8R8A8UNorm && format.ColorSpace == ColorSpace.SrgbNonlinear)
                {
                    return format;
                }
            }

            return availableFormats[0];
        }

        private void CreateCommandBuffers()
        {
            commandBuffers = device.AllocateCommandBuffers(commandPool, CommandBufferLevel.Primary, (uint) frameBuffers.Length);

            for (int index = 0; index < frameBuffers.Length; index++)
            {
                CommandBuffer commandBuffer = commandBuffers[index];

                commandBuffer.Begin(CommandBufferUsageFlags.SimultaneousUse);

                commandBuffer.BeginRenderPass(renderPass,
                                                frameBuffers[index],
                                                new Rect2D(swapChainExtent),
                                                new ClearValue(),
                                                SubpassContents.Inline);

                commandBuffer.BindPipeline(PipelineBindPoint.Graphics, pipeline);

                commandBuffer.Draw(3, 1, 0, 0);

                commandBuffer.EndRenderPass();

                commandBuffer.End();
            }
        }

        private void CreateCommandPool()
        {
            QueueFamilyIndices queueFamilies = FindQueueFamilies(physicalDevice);

            commandPool = device.CreateCommandPool(queueFamilies.GraphicsFamily.Value);
        }

        private void CreateFrameBuffers()
        {
            Framebuffer Create(ImageView imageView)
            {
                return device.CreateFramebuffer(renderPass,
                                           imageView,
                                           swapChainExtent.Width,
                                           swapChainExtent.Height,
                                           1);
            }

            frameBuffers = swapChainImageViews.Select(Create).ToArray();
        }

        private void CreateGraphicsPipeline()
        {
            pipelineLayout = device.CreatePipelineLayout(null, null);

            pipeline = device.CreateGraphicsPipeline(null,
            new[]
            {
                new PipelineShaderStageCreateInfo
                {
                    Stage = ShaderStageFlags.Vertex,
                    Module = vertShader,
                    Name = "main"
                },
                new PipelineShaderStageCreateInfo
                {
                    Stage = ShaderStageFlags.Fragment,
                    Module = fragShader,
                    Name = "main"
                }
            },
            new PipelineVertexInputStateCreateInfo(),
            new PipelineInputAssemblyStateCreateInfo
            {
                PrimitiveRestartEnable = false,
                Topology = PrimitiveTopology.TriangleList
            },
            new PipelineRasterizationStateCreateInfo
            {
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1,
                CullMode = CullModeFlags.Back,
                FrontFace = FrontFace.Clockwise,
                DepthBiasEnable = false
            },
            pipelineLayout,
            renderPass,
            0,
            null,
            -1,
            viewportState: new PipelineViewportStateCreateInfo
            {
                Viewports = new[]
                {
                    new Viewport(0f, 0f, swapChainExtent.Width, swapChainExtent.Height, 0, 1)
                },
                Scissors = new[]
                {
                    new Rect2D(swapChainExtent)
                }
            },
            colorBlendState: new PipelineColorBlendStateCreateInfo
            {
                Attachments = new[]
                {
                    new PipelineColorBlendAttachmentState
                    {
                        ColorWriteMask = ColorComponentFlags.R
                                            | ColorComponentFlags.G
                                            | ColorComponentFlags.B
                                            | ColorComponentFlags.A,
                        BlendEnable = false
                    }
                },
                LogicOpEnable = false
            },
            multisampleState: new PipelineMultisampleStateCreateInfo
            {
                SampleShadingEnable = false,
                RasterizationSamples = SampleCountFlags.SampleCount1,
                MinSampleShading = 1
            });
        }

        private void CreateImageViews()
        {
            swapChainImageViews = swapChainImages.Select(image => device.CreateImageView(image,
                                                                                        ImageViewType.ImageView2d,
                                                                                        swapChainFormat,
                                                                                        ComponentMapping.Identity,
                                                                                        new ImageSubresourceRange(ImageAspectFlags.Color, 0, 1, 0, 1)))
                                                .ToArray();
        }

        private void CreateInstance()
        {
            List<string> enabledLayers = new List<string>();

            //VK_LAYER_LUNARG_api_dump
            //VK_LAYER_LUNARG_standard_validation

            void AddAvailableLayer(string layerName)
            {
                if (Instance.EnumerateLayerProperties().Any(x => x.LayerName == layerName))
                {
                    enabledLayers.Add(layerName);
                }
            }

            AddAvailableLayer("VK_LAYER_LUNARG_standard_validation");

            instance = Instance.Create(
                enabledLayers.ToArray(),
                Glfw3.GetRequiredInstanceExtensions().Append(ExtExtensions.DebugReport).ToArray(),
                applicationInfo: new ApplicationInfo
                {
                    ApplicationName = "Hello Triangle",
                    ApplicationVersion = new SharpVk.Version(1, 0, 0),
                    EngineName = "SharpVk",
                    EngineVersion = new SharpVk.Version(0, 4, 1),
                    ApiVersion = new SharpVk.Version(1, 0, 0)
                });

            instance.CreateDebugReportCallback(DebugReportDelegate, DebugReportFlags.Error | DebugReportFlags.Warning);
        }

        private void CreateLogicalDevice()
        {
            QueueFamilyIndices queueFamilies = FindQueueFamilies(physicalDevice);

            device = physicalDevice.CreateDevice(queueFamilies.Indices.Select(index => new DeviceQueueCreateInfo
            {
                QueueFamilyIndex = index,
                QueuePriorities = new[] { 1f }
            }).ToArray(),
            null,
            KhrExtensions.Swapchain);

            graphicsQueue = device.GetQueue(queueFamilies.GraphicsFamily.Value, 0);
            presentQueue = device.GetQueue(queueFamilies.PresentFamily.Value, 0);
        }

        private void CreateRenderPass()
        {
            renderPass = device.CreateRenderPass(
                new AttachmentDescription
                {
                    Format = swapChainFormat,
                    Samples = SampleCountFlags.SampleCount1,
                    LoadOp = AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.Undefined,
                    FinalLayout = ImageLayout.PresentSource
                },
                new SubpassDescription
                {
                    DepthStencilAttachment = new AttachmentReference
                    {
                        Attachment = Constants.AttachmentUnused
                    },
                    PipelineBindPoint = PipelineBindPoint.Graphics,
                    ColorAttachments = new[]
                    {
                        new AttachmentReference
                        {
                            Attachment = 0,
                            Layout = ImageLayout.ColorAttachmentOptimal
                        }
                    }
                },
                new[]
                {
                    new SubpassDependency
                    {
                        SourceSubpass = Constants.SubpassExternal,
                        DestinationSubpass = 0,
                        SourceStageMask = PipelineStageFlags.BottomOfPipe,
                        SourceAccessMask = AccessFlags.MemoryRead,
                        DestinationStageMask = PipelineStageFlags.ColorAttachmentOutput,
                        DestinationAccessMask = AccessFlags.ColorAttachmentRead | AccessFlags.ColorAttachmentWrite
                    },
                    new SubpassDependency
                    {
                        SourceSubpass = 0,
                        DestinationSubpass = Constants.SubpassExternal,
                        SourceStageMask = PipelineStageFlags.ColorAttachmentOutput,
                        SourceAccessMask = AccessFlags.ColorAttachmentRead | AccessFlags.ColorAttachmentWrite,
                        DestinationStageMask = PipelineStageFlags.BottomOfPipe,
                        DestinationAccessMask = AccessFlags.MemoryRead
                    }
                });
        }

        private void CreateSemaphores()
        {
            imageAvailableSemaphore = device.CreateSemaphore();
            renderFinishedSemaphore = device.CreateSemaphore();
        }

        private void CreateShaderModules()
        {
            ShaderModule CreateShader(string path)
            {
                uint[] shaderData = LoadShaderData(path, out int codeSize);

                return device.CreateShaderModule(codeSize, shaderData);
            }

            vertShader = CreateShader(@".\Shaders\vert.spv");

            fragShader = CreateShader(@".\Shaders\frag.spv");
        }

        private void CreateSurface()
        {
            surface = instance.CreateGlfw3Surface(window);
        }

        private void CreateSwapChain()
        {
            SwapChainSupportDetails swapChainSupport = QuerySwapChainSupport(physicalDevice);

            uint imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
            if (swapChainSupport.Capabilities.MaxImageCount > 0 && imageCount > swapChainSupport.Capabilities.MaxImageCount)
            {
                imageCount = swapChainSupport.Capabilities.MaxImageCount;
            }

            SurfaceFormat surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);

            QueueFamilyIndices queueFamilies = FindQueueFamilies(physicalDevice);

            uint[] indices = queueFamilies.Indices.ToArray();

            Extent2D extent = ChooseSwapExtent(swapChainSupport.Capabilities);

            swapChain = device.CreateSwapchain(surface,
                                                    imageCount,
                                                    surfaceFormat.Format,
                                                    surfaceFormat.ColorSpace,
                                                    extent,
                                                    1,
                                                    ImageUsageFlags.ColorAttachment,
                                                    indices.Length == 1
                                                        ? SharingMode.Exclusive
                                                        : SharingMode.Concurrent,
                                                    indices,
                                                    swapChainSupport.Capabilities.CurrentTransform,
                                                    CompositeAlphaFlags.Opaque,
                                                    ChooseSwapPresentMode(swapChainSupport.PresentModes),
                                                    true,
                                                    swapChain);

            swapChainImages = swapChain.GetImages();
            swapChainFormat = surfaceFormat.Format;
            swapChainExtent = extent;
        }

        private void DrawFrame()
        {
            uint nextImage = swapChain.AcquireNextImage(uint.MaxValue, imageAvailableSemaphore, null);

            graphicsQueue.Submit(
                new SubmitInfo
                {
                    CommandBuffers = new[] { commandBuffers[nextImage] },
                    SignalSemaphores = new[] { renderFinishedSemaphore },
                    WaitDestinationStageMask = new[] { PipelineStageFlags.ColorAttachmentOutput },
                    WaitSemaphores = new[] { imageAvailableSemaphore }
                },
                null);

            presentQueue.Present(renderFinishedSemaphore, swapChain, nextImage, new Result[1]);
        }

        private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
        {
            uint? graphicsFamily = null, presentFamily = null;

            QueueFamilyProperties[] queueFamilies = device.GetQueueFamilyProperties();

            for (uint index = 0; index < queueFamilies.Length && !QueueFamilyIndices.IsComplete(graphicsFamily, presentFamily); index++)
            {
                if (queueFamilies[index].QueueFlags.HasFlag(QueueFlags.Graphics))
                {
                    graphicsFamily = index;
                }

                if (device.GetSurfaceSupport(index, surface))
                {
                    presentFamily = index;
                }
            }

            return new QueueFamilyIndices(graphicsFamily, presentFamily);
        }

        private void InitialiseVulkan()
        {
            CreateInstance();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateSwapChain();
            CreateImageViews();
            CreateRenderPass();
            CreateShaderModules();
            CreateGraphicsPipeline();
            CreateFrameBuffers();
            CreateCommandPool();
            CreateCommandBuffers();
            CreateSemaphores();
        }

        private void InitialiseWindow()
        {
            Glfw3.Init();

            Glfw3.WindowHint(0x00022001, 0);
            window = Glfw3.CreateWindow(SurfaceWidth, SurfaceHeight, "Hello Triangle", IntPtr.Zero, IntPtr.Zero);
            windowSizeCallback = (window, width, height) => RecreateSwapChain();

            Glfw3.SetWindowSizeCallback(window, windowSizeCallback);
        }

        private bool IsSuitableDevice(PhysicalDevice device)
        {
            return device.EnumerateDeviceExtensionProperties(null).Any(extension => extension.ExtensionName == KhrExtensions.Swapchain)
                    && QueueFamilyIndices.IsComplete(FindQueueFamilies(device));
        }

        private void MainLoop()
        {
            while (!Glfw3.WindowShouldClose(window))
            {
                DrawFrame();

                Glfw3.PollEvents();
            }
        }

        private void PickPhysicalDevice()
        {
            PhysicalDevice[] availableDevices = instance.EnumeratePhysicalDevices();

            physicalDevice = availableDevices.First(IsSuitableDevice);
        }

        private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice device)
        {
            return new SwapChainSupportDetails(device.GetSurfaceCapabilities(surface), device.GetSurfaceFormats(surface), device.GetSurfacePresentModes(surface));
        }

        private void RecreateSwapChain()
        {
            device.WaitIdle();

            commandPool.FreeCommandBuffers(commandBuffers);

            foreach (Framebuffer frameBuffer in frameBuffers)
            {
                frameBuffer.Dispose();
            }
            //frameBuffers = null;

            pipeline.Dispose();
            //pipeline = null;

            pipelineLayout.Dispose();
            //pipelineLayout = null;

            foreach (ImageView imageView in swapChainImageViews)
            {
                imageView.Dispose();
            }
            //swapChainImageViews = null;

            renderPass.Dispose();
            //renderPass = null;

            swapChain.Dispose();
            //swapChain = null;

            CreateSwapChain();
            CreateImageViews();
            CreateRenderPass();
            CreateGraphicsPipeline();
            CreateFrameBuffers();
            CreateCommandBuffers();
        }

        private void TearDown()
        {
            device.WaitIdle();

            renderFinishedSemaphore.Dispose();
            //renderFinishedSemaphore = null;

            imageAvailableSemaphore.Dispose();
            //imageAvailableSemaphore = null;

            commandPool.Dispose();
            //commandPool = null;

            foreach (Framebuffer frameBuffer in frameBuffers)
            {
                frameBuffer.Dispose();
            }
            //frameBuffers = null;

            fragShader.Dispose();
            //fragShader = null;

            vertShader.Dispose();
            //vertShader = null;

            pipeline.Dispose();
            //pipeline = null;

            pipelineLayout.Dispose();
            //pipelineLayout = null;

            foreach (ImageView imageView in swapChainImageViews)
            {
                imageView.Dispose();
            }
            //swapChainImageViews = null;

            renderPass.Dispose();
            //renderPass = null;

            swapChain.Dispose();
            //swapChain = null;

            device.Dispose();
            //device = null;

            surface.Dispose();
            //surface = null;

            instance.Dispose();
            //instance = null;
        }

        public Extent2D ChooseSwapExtent(SurfaceCapabilities capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            }
            else
            {
                return new Extent2D
                {
                    Width = Math.Max(capabilities.MinImageExtent.Width, Math.Min(capabilities.MaxImageExtent.Width, SurfaceWidth)),
                    Height = Math.Max(capabilities.MinImageExtent.Height, Math.Min(capabilities.MaxImageExtent.Height, SurfaceHeight))
                };
            }
        }

        public void Run()
        {
            InitialiseWindow();
            InitialiseVulkan();
            MainLoop();
            TearDown();
        }

        private readonly struct QueueFamilyIndices
        {
            public readonly uint? GraphicsFamily;
            public readonly uint? PresentFamily;

            public QueueFamilyIndices(uint? graphicsFamily, uint? presentFamily)
            {
                GraphicsFamily = graphicsFamily;

                PresentFamily = presentFamily;
            }

            public IEnumerable<uint> Indices
            {
                get
                {
                    if (GraphicsFamily.HasValue)
                    {
                        yield return GraphicsFamily.Value;
                    }

                    if (PresentFamily.HasValue && PresentFamily != GraphicsFamily)
                    {
                        yield return PresentFamily.Value;
                    }
                }
            }

            public static bool IsComplete(QueueFamilyIndices indices)
            {
                return indices.GraphicsFamily.HasValue && indices.PresentFamily.HasValue;
            }

            public static bool IsComplete(uint? graphicsFamily, uint? presentFamily)
            {
                return graphicsFamily.HasValue && presentFamily.HasValue;
            }
        }

        private readonly struct SwapChainSupportDetails
        {
            public readonly SurfaceCapabilities Capabilities;
            public readonly SurfaceFormat[] Formats;
            public readonly PresentMode[] PresentModes;

            public SwapChainSupportDetails(in SurfaceCapabilities surfaceCapabilities, in SurfaceFormat[] surfaceFormats, in PresentMode[] presentModes)
            {
                Capabilities = surfaceCapabilities;

                Formats = surfaceFormats;

                PresentModes = presentModes;
            }
        }
    }
}