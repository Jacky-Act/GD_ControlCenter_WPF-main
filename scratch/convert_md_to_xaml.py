import re

with open('Assets/Help.md', 'r', encoding='utf-8') as f:
    lines = f.readlines()

xaml = []
xaml.append("""<UserControl x:Class="GD_ControlCenter_WPF.Views.Pages.HelpView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" 
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008" 
             xmlns:materialDesign="http://materialdesigninxaml.net/winfx/xaml/themes"
             mc:Ignorable="d" 
             d:DesignHeight="800" d:DesignWidth="1200"
             TextElement.FontFamily="Microsoft YaHei, Segoe UI"
             TextElement.FontSize="14"
             TextElement.Foreground="{DynamicResource MaterialDesignBody}"
             Background="{DynamicResource MaterialDesignPaper}">
    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Padding="20">
        <StackPanel MaxWidth="1000" HorizontalAlignment="Left" Margin="20">
""")

def parse_line(line):
    line = line.strip()
    if not line:
        return ""
    
    if line.startswith("# "):
        return f'<TextBlock Text="{line[2:]}" FontSize="32" FontWeight="Bold" Margin="0,0,0,10" Foreground="{{DynamicResource PrimaryHueMidBrush}}"/>'
    elif line.startswith("## "):
        return f'<TextBlock Text="{line[3:]}" FontSize="24" FontWeight="Bold" Margin="0,20,0,10" Foreground="{{DynamicResource PrimaryHueMidBrush}}"/>'
    elif line.startswith("### "):
        return f'<TextBlock Text="{line[4:]}" FontSize="18" FontWeight="Bold" Margin="0,15,0,5" Foreground="{{DynamicResource PrimaryHueMidBrush}}"/>'
    else:
        # Check for list items
        margin = "0,5,0,5"
        if line.startswith("* ") or line.startswith("- "):
            line = line[2:]
            margin = "20,5,0,5"
            line = "• " + line
        elif line.startswith("1. "):
            line = line[3:]
            margin = "20,5,0,5"
            line = "1. " + line
        elif line.startswith("2. "):
            line = line[3:]
            margin = "20,5,0,5"
            line = "2. " + line
        elif line.startswith("3. "):
            line = line[3:]
            margin = "20,5,0,5"
            line = "3. " + line
        elif line.startswith("4. "):
            line = line[3:]
            margin = "20,5,0,5"
            line = "4. " + line
            
        # Parse inline bold
        parts = re.split(r'(\*\*.*?\*\*)', line)
        inlines = []
        for p in parts:
            if p.startswith("**") and p.endswith("**"):
                inlines.append(f'<Run Text="{p[2:-2]}" FontWeight="Bold"/>')
            else:
                p = p.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('"', '&quot;')
                if p:
                    inlines.append(f'<Run Text="{p}"/>')
        
        inlines_str = "".join(inlines)
        return f'<TextBlock TextWrapping="Wrap" Margin="{margin}">{inlines_str}</TextBlock>'

for line in lines:
    if line.startswith("欢迎使用"):
         xaml.append(f'<TextBlock Text="{line.strip()}" TextWrapping="Wrap" Margin="0,0,0,30" Opacity="0.8"/>')
         continue
         
    if line.strip().startswith("* ") and "    * " in line:
        # nested
        line = line.replace("    * ", "• ")
        res = parse_line(line)
        res = res.replace('Margin="20,5,0,5"', 'Margin="60,2,0,2"')
        xaml.append(res)
    elif line.strip().startswith("* ") and "  * " in line:
        line = line.replace("  * ", "• ")
        res = parse_line(line)
        res = res.replace('Margin="20,5,0,5"', 'Margin="40,2,0,2"')
        xaml.append(res)
    elif line.strip().startswith("1. ") and "  1. " in line:
        line = line.replace("  1. ", "1. ")
        res = parse_line(line)
        res = res.replace('Margin="20,5,0,5"', 'Margin="40,2,0,2"')
        xaml.append(res)
    elif line.strip().startswith("2. ") and "  2. " in line:
        line = line.replace("  2. ", "2. ")
        res = parse_line(line)
        res = res.replace('Margin="20,5,0,5"', 'Margin="40,2,0,2"')
        xaml.append(res)
    elif line.strip().startswith("3. ") and "  3. " in line:
        line = line.replace("  3. ", "3. ")
        res = parse_line(line)
        res = res.replace('Margin="20,5,0,5"', 'Margin="40,2,0,2"')
        xaml.append(res)
    elif line.strip().startswith("4. ") and "  4. " in line:
        line = line.replace("  4. ", "4. ")
        res = parse_line(line)
        res = res.replace('Margin="20,5,0,5"', 'Margin="40,2,0,2"')
        xaml.append(res)
    else:
        res = parse_line(line)
        if res:
            xaml.append(res)

xaml.append("""
        </StackPanel>
    </ScrollViewer>
</UserControl>
""")

with open('Views/Pages/HelpView.xaml', 'w', encoding='utf-8') as f:
    f.write("\n".join(xaml))

print("XAML successfully generated.")
