namespace Tersus.Core.Knowledge;

public enum FileCategory
{
    Other,
    Video,
    Audio,
    Image,
    Document,
    Archive,
    Installer,
    Code,
    DiskImage,
    Database,
    Log,
    Temporary,
    System,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    public static FileCategory Of(string extension)
    {
        string e = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        return Map.GetValueOrDefault(e, FileCategory.Other);
    }

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Video => "Vídeos",
        FileCategory.Audio => "Áudio",
        FileCategory.Image => "Imagens",
        FileCategory.Document => "Documentos",
        FileCategory.Archive => "Arquivos compactados",
        FileCategory.Installer => "Instaladores e programas",
        FileCategory.Code => "Código e desenvolvimento",
        FileCategory.DiskImage => "Imagens de disco e máquinas virtuais",
        FileCategory.Database => "Bancos de dados",
        FileCategory.Log => "Registros (logs)",
        FileCategory.Temporary => "Temporários",
        FileCategory.System => "Sistema",
        _ => "Outros",
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        void Add(FileCategory c, string list)
        {
            foreach (string e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                d["." + e] = c;
            }
        }

        Add(FileCategory.Video, "mp4 mkv avi mov wmv webm flv m4v mpg mpeg 3gp ts m2ts vob");
        Add(FileCategory.Audio, "mp3 flac wav m4a aac ogg wma opus aiff mid");
        Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd ai svg ico xcf");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt md rtf csv epub mobi pages numbers key tex");
        Add(FileCategory.Archive, "zip rar 7z tar gz bz2 xz tgz zst cab lzh");
        Add(FileCategory.Installer, "exe msi msix appx appxbundle msp dll apk");
        Add(FileCategory.Code, "cs js ts jsx tsx py java cpp c h hpp go rs rb php html css scss json xml yml yaml toml sln csproj pdb obj o class jar pyc");
        Add(FileCategory.DiskImage, "iso img vhd vhdx vmdk vdi ova ovf wim esd qcow2 dmg");
        Add(FileCategory.Database, "db sqlite sqlite3 mdb accdb mdf ldf bak sql pst ost edb");
        Add(FileCategory.Log, "log etl evtx dmp mdmp trace");
        Add(FileCategory.Temporary, "tmp temp crdownload part partial download old");
        Add(FileCategory.System, "sys drv dat cat mui nls inf");
        return d;
    }
}
