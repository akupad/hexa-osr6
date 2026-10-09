using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Hexa.Services.Sr6;

namespace Hexa.CoreTests;

/// <summary>
/// 开发期探针：把 SR6 模型用 WPF 的 RenderTargetBitmap 离屏渲染成 PNG，
/// 用来肉眼确认模型朝向/装配是否正确（只在设置 HELIX_SR6_RENDER 环境变量时运行）。
/// </summary>
internal static class Sr6RenderProbe
{
    public static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var rig = new Sr6Rig();
        rig.Update([0.5, 0.5, 0.5, 0.5, 0.5, 0.5], [1, 1, 1, 1, 1, 1]);

        Rect3D bounds = rig.Root.Bounds;
        var center = new Point3D(
            bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double radius = 0.5 * Math.Sqrt(
            bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ);

        Console.WriteLine($"bounds X[{bounds.X:F0},{bounds.X + bounds.SizeX:F0}] " +
                          $"Y[{bounds.Y:F0},{bounds.Y + bounds.SizeY:F0}] " +
                          $"Z[{bounds.Z:F0},{bounds.Z + bounds.SizeZ:F0}] center {center} r={radius:F0}");

        // 原始模型坐标（不做 osr-emu 的 orientation 旋转）下的两种“上方向”假设
        rig.Root.Transform = Transform3D.Identity;
        var rawBounds = rig.Root.Bounds;
        center = new Point3D(
            rawBounds.X + rawBounds.SizeX / 2,
            rawBounds.Y + rawBounds.SizeY / 2,
            rawBounds.Z + rawBounds.SizeZ / 2);
        radius = 0.5 * Math.Sqrt(
            rawBounds.SizeX * rawBounds.SizeX + rawBounds.SizeY * rawBounds.SizeY +
            rawBounds.SizeZ * rawBounds.SizeZ);
        Console.WriteLine($"raw bounds X[{rawBounds.X:F0},{rawBounds.X + rawBounds.SizeX:F0}] " +
                          $"Y[{rawBounds.Y:F0},{rawBounds.Y + rawBounds.SizeY:F0}] " +
                          $"Z[{rawBounds.Z:F0},{rawBounds.Z + rawBounds.SizeZ:F0}] r={radius:F0}");

        var views = new (string Name, Vector3D Up, Vector3D Direction)[]
        {
            ("raw-z-up", new Vector3D(0, 0, 1), new Vector3D(0.55, -0.75, 0.35)),
            ("raw-y-up", new Vector3D(0, 1, 0), new Vector3D(0.55, -0.55, 0.62)),
            ("raw-z-up-side", new Vector3D(0, 0, 1), new Vector3D(1.0, -0.05, 0.08)),
            ("raw-y-up-side", new Vector3D(0, 1, 0), new Vector3D(1.0, -0.05, 0.08)),
        };

        foreach ((string name, Vector3D up, Vector3D direction) in views)
        {
            direction.Normalize();
            double distance = radius * 2.1;
            var camera = new PerspectiveCamera
            {
                Position = center - direction * distance,
                LookDirection = direction,
                UpDirection = up,
                FieldOfView = 45,
                NearPlaneDistance = 1,
                FarPlaneDistance = 20000,
            };

            var viewport = new Viewport3D { Camera = camera };
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x66, 0x70)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0xE0, 0xE8, 0xF2), new Vector3D(-0.5, -0.8, -0.6)));
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x68, 0x80), new Vector3D(0.8, 0.2, 0.5)));
            viewport.Children.Add(new ModelVisual3D { Content = lights });
            viewport.Children.Add(new ModelVisual3D { Content = rig.Root });

            const int width = 900, height = 640;
            viewport.Width = width;
            viewport.Height = height;
            viewport.Measure(new Size(width, height));
            viewport.Arrange(new Rect(0, 0, width, height));
            viewport.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(viewport);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(outputDirectory, $"sr6-{name}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine($"rendered {path}");
        }
    }
}
