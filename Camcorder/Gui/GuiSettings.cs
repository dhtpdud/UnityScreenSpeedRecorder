using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace UnityCamcorderGui
{
    // 창에서 고른 값. 다음에 열 때 그대로 복원하도록 %LOCALAPPDATA%\UnityCamcorder\gui.json 에 저장합니다.
    [DataContract]
    public sealed class GuiSettings
    {
        [DataMember] public bool AttachToRunning { get; set; }        // false = exe 실행, true = 실행 중인 앱에 연결
        [DataMember] public string TargetExe { get; set; } = "";
        [DataMember] public string TargetArgs { get; set; } = "";
        [DataMember] public string OutputPath { get; set; } = "";
        [DataMember] public int Fps { get; set; } = 60;
        [DataMember] public int LengthMode { get; set; }              // 0 = 초, 1 = 프레임, 2 = 끝날 때까지
        [DataMember] public double DurationSeconds { get; set; } = 10;
        [DataMember] public int TotalFrames { get; set; } = 600;
        [DataMember] public string Codec { get; set; } = "auto";
        [DataMember] public int Quality { get; set; } = 23;
        [DataMember] public string FfmpegPath { get; set; } = "";
        [DataMember] public bool CloseTargetWhenDone { get; set; } = true;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityCamcorder");

        private static string FilePath => Path.Combine(Folder, "gui.json");

        public static GuiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var stream = File.OpenRead(FilePath))
                        return (GuiSettings?)new DataContractJsonSerializer(typeof(GuiSettings)).ReadObject(stream) ?? new GuiSettings();
            }
            catch { /* 망가진 파일이면 기본값으로 시작 */ }
            return new GuiSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                using (var stream = File.Create(FilePath))
                    new DataContractJsonSerializer(typeof(GuiSettings)).WriteObject(stream, this);
            }
            catch { /* 저장 실패는 무시 (다음에 기본값으로 열릴 뿐) */ }
        }
    }

    // UnityCamcorder.exe --config 로 넘기는 설정. 속성 이름은 캠코더의 camcorder.json(Settings)과 같습니다.
    // 대상은 TargetExe / TargetPid 중 하나만 채워, exe 옆 camcorder.json과 섞이지 않도록 따로 파일을 만듭니다.
    [DataContract]
    public sealed class RunConfig
    {
        [DataMember(EmitDefaultValue = false)] public string? TargetExe { get; set; }
        [DataMember(EmitDefaultValue = false)] public string? TargetArgs { get; set; }
        [DataMember] public int TargetPid { get; set; }
        [DataMember] public string OutputPath { get; set; } = "";
        [DataMember] public int Fps { get; set; }
        [DataMember] public int TotalFrames { get; set; }
        [DataMember] public double DurationSeconds { get; set; }
        [DataMember] public string Codec { get; set; } = "auto";
        [DataMember] public int Quality { get; set; }
        [DataMember(EmitDefaultValue = false)] public string? FfmpegPath { get; set; }
        [DataMember] public bool CloseTargetWhenDone { get; set; }

        public string Write()
        {
            Directory.CreateDirectory(GuiSettings.Folder);
            string path = Path.Combine(GuiSettings.Folder, "gui-run.json");
            using (var stream = File.Create(path))
                new DataContractJsonSerializer(typeof(RunConfig)).WriteObject(stream, this);
            return path;
        }
    }
}
