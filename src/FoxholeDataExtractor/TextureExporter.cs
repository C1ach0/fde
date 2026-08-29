using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

namespace FoxholeDataExtractor;

public static class TextureExporter
{
    public static bool TryExport(DefaultFileProvider provider, string rawReference, string destination)
    {
        foreach (var packagePath in PackageCandidates(rawReference))
        {
            try
            {
                var file = provider.Files.Values.FirstOrDefault(f => f.IsUePackage &&
                    string.Equals(WithoutExtension(f.Path), WithoutExtension(packagePath), StringComparison.OrdinalIgnoreCase));
                var package = file != null ? provider.LoadPackage(file) : provider.LoadPackage(packagePath);
                var texture = package.GetExports().OfType<UTexture>().FirstOrDefault();
                if (texture is null) continue;
                var bitmap = texture.Decode(ETexturePlatform.DesktopMobile);
                if (bitmap is null) continue;
                var bytes = bitmap.Encode(ETextureFormat.Png, false, out _);
                if (bytes is null || bytes.Length == 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
                return true;
            }
            catch { /* caller decides whether a missing resource is important */ }
        }
        return false;
    }

    private static IEnumerable<string> PackageCandidates(string value)
    {
        var s=value.Trim().Trim('"','\'').Replace('\\','/'); var quote=s.IndexOf('\'');
        if (quote>=0 && s.EndsWith("'")) s=s[(quote+1)..^1];
        var dot=s.LastIndexOf('.'); if(dot>s.LastIndexOf('/')) s=s[..dot]; s=s.TrimStart('/');
        yield return s;
        if(s.StartsWith("Game/",StringComparison.OrdinalIgnoreCase)) yield return "War/Content/"+s[5..];
        else if(s.StartsWith("War/Content/",StringComparison.OrdinalIgnoreCase)) yield return "Game/"+s["War/Content/".Length..];
    }
    private static string WithoutExtension(string s) { s=s.Replace('\\','/').TrimStart('/'); if(s.EndsWith(".uasset",StringComparison.OrdinalIgnoreCase)) s=s[..^7]; if(s.EndsWith(".umap",StringComparison.OrdinalIgnoreCase)) s=s[..^5]; return s; }
}
