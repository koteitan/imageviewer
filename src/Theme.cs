using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ImageViewer;

/// <summary>Dark theme shared by the right-click view and the dialogs.</summary>
internal static class Theme
{
    public static ResourceDictionary Load() => (ResourceDictionary)XamlReader.Parse(ThemeXaml);

    /// <summary>Font for everything inside <paramref name="root"/>.</summary>
    public static void ApplyText(Border root)
    {
        System.Windows.Documents.TextElement.SetFontFamily(root, new FontFamily("Segoe UI Variable Text, Yu Gothic UI, Segoe UI"));
        System.Windows.Documents.TextElement.SetFontSize(root, 13);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
    }

    /// <summary>Dark theme in the style of Windows 11 (Fluent) flyouts.</summary>
    private const string ThemeXaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="CardBrush" Color="#202020"/>
  <SolidColorBrush x:Key="CardBorderBrush" Color="#3A3A3A"/>
  <SolidColorBrush x:Key="TextBrush" Color="#F3F3F3"/>
  <SolidColorBrush x:Key="LabelBrush" Color="#A6A6A6"/>
  <SolidColorBrush x:Key="ControlBrush" Color="#2D2D2D"/>
  <SolidColorBrush x:Key="ControlHoverBrush" Color="#383838"/>
  <SolidColorBrush x:Key="ControlPressedBrush" Color="#262626"/>
  <SolidColorBrush x:Key="ControlBorderBrush" Color="#3F3F3F"/>
  <SolidColorBrush x:Key="InputBrush" Color="#1A1A1A"/>
  <SolidColorBrush x:Key="AccentBrush" Color="#4CC2FF"/>
  <SolidColorBrush x:Key="AccentHoverBrush" Color="#48B1E8"/>
  <SolidColorBrush x:Key="AccentPressedBrush" Color="#42A1D2"/>
  <SolidColorBrush x:Key="SuccessBrush" Color="#6CCB5F"/>
  <SolidColorBrush x:Key="ErrorBrush" Color="#FF99A4"/>

  <Style x:Key="Card" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource CardBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource CardBorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="8"/>
    <Setter Property="Padding" Value="16,10,8,10"/>
  </Style>

  <Style x:Key="Label" TargetType="TextBlock">
    <Setter Property="Foreground" Value="{StaticResource LabelBrush}"/>
    <Setter Property="FontSize" Value="12"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
    <Setter Property="Margin" Value="0,4,16,4"/>
  </Style>

  <Style x:Key="Unit" TargetType="TextBlock">
    <Setter Property="Foreground" Value="{StaticResource LabelBrush}"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
    <Setter Property="Margin" Value="6,0,0,0"/>
  </Style>

  <Style x:Key="SubtleButton" TargetType="Button">
    <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
    <Setter Property="Background" Value="{StaticResource ControlBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource ControlBorderBrush}"/>
    <Setter Property="Padding" Value="12,4"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="{StaticResource ControlHoverBrush}"/>
      </Trigger>
      <Trigger Property="IsPressed" Value="True">
        <Setter Property="Background" Value="{StaticResource ControlPressedBrush}"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource SubtleButton}">
    <Setter Property="Foreground" Value="#000000"/>
    <Setter Property="Background" Value="{StaticResource AccentBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource AccentBrush}"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Style.Triggers>
      <Trigger Property="IsMouseOver" Value="True">
        <Setter Property="Background" Value="{StaticResource AccentHoverBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource AccentHoverBrush}"/>
      </Trigger>
      <Trigger Property="IsPressed" Value="True">
        <Setter Property="Background" Value="{StaticResource AccentPressedBrush}"/>
        <Setter Property="BorderBrush" Value="{StaticResource AccentPressedBrush}"/>
      </Trigger>
      <Trigger Property="IsEnabled" Value="False">
        <Setter Property="Opacity" Value="0.35"/>
        <Setter Property="Cursor" Value="Arrow"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType="TextBox">
    <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
    <Setter Property="Background" Value="{StaticResource ControlBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource ControlBorderBrush}"/>
    <Setter Property="CaretBrush" Value="{StaticResource TextBrush}"/>
    <Setter Property="SelectionBrush" Value="{StaticResource AccentBrush}"/>
    <Setter Property="Padding" Value="8,0"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="4" SnapsToDevicePixels="True">
            <Grid>
              <ScrollViewer x:Name="PART_ContentHost" Focusable="False" Margin="{TemplateBinding Padding}"
                            VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                            HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
              <Border x:Name="Underline" Height="2" VerticalAlignment="Bottom" Margin="1,0"
                      CornerRadius="0,0,3,3" Background="Transparent"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Box" Property="Background" Value="{StaticResource ControlHoverBrush}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Box" Property="Background" Value="{StaticResource InputBrush}"/>
              <Setter TargetName="Underline" Property="Background" Value="{StaticResource AccentBrush}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";
}
