using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.IO;

namespace GD_ControlCenter_WPF.ViewModels
{
    public partial class HelpViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _markdownText = "";

        public HelpViewModel()
        {
            LoadHelpDocument();
        }

        private void LoadHelpDocument()
        {
            try
            {
                string helpFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Help.md");
                if (File.Exists(helpFilePath))
                {
                    MarkdownText = File.ReadAllText(helpFilePath);
                }
                else
                {
                    MarkdownText = "# 找不到帮助文档\n\n请确保 Assets/Help.md 文件存在。";
                }
            }
            catch (Exception ex)
            {
                MarkdownText = $"# 读取帮助文档失败\n\n{ex.Message}";
            }
        }
    }
}
