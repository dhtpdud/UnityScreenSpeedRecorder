using System;
using System.Windows.Forms;

namespace UnityCamcorderGui
{
    // UnityCamcorder.exe(명령줄 캠코더)를 버튼 몇 개로 쓸 수 있게 해 주는 창.
    // 녹화 자체는 옆에 있는 UnityCamcorder.exe가 하고, 이 창은 설정을 넘기고 진행 상황을 보여 줍니다.
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
