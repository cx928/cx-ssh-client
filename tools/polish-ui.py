#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""界面美化: 标题栏/欢迎页使用应用图标, 会话列表增加空状态提示"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

XAML = r"E:\Documents\deepseek-harness\default-workspace\cx-ssh-client\MainWindow.xaml"
text = open(XAML, encoding="utf-8").read()
orig = text

# ---------- 1. 标题栏图标 ----------
titlebar_old = """                <Border Width="22" Height="22" CornerRadius="6">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                            <GradientStop Color="#5B8DEF" Offset="0"/>
                            <GradientStop Color="#8B5CF6" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.Background>
                    <FontIcon Glyph="&#xE756;" FontSize="12" Foreground="White"/>
                </Border>"""
titlebar_new = """                <Image x:Name="TitleBarIcon" Width="26" Height="26" VerticalAlignment="Center"/>"""
if titlebar_old in text:
    text = text.replace(titlebar_old, titlebar_new)
    print("标题栏图标: 已替换")
else:
    print("!! 标题栏图标: 未匹配")

# ---------- 2. 欢迎页图标 ----------
welcome_old = """                        <Border Width="72" Height="72" CornerRadius="20" HorizontalAlignment="Center">
                            <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                                    <GradientStop Color="#5B8DEF" Offset="0"/>
                                    <GradientStop Color="#8B5CF6" Offset="1"/>
                                </LinearGradientBrush>
                            </Border.Background>
                            <FontIcon Glyph="&#xE756;" FontSize="34" Foreground="White"/>
                        </Border>"""
welcome_new = """                        <Image x:Name="WelcomeIcon" Width="112" Height="112" HorizontalAlignment="Center"/>"""
if welcome_old in text:
    text = text.replace(welcome_old, welcome_new)
    print("欢迎页图标: 已替换")
else:
    print("!! 欢迎页图标: 未匹配")

# ---------- 3. 会话列表空状态 ----------
anchor = """                    <StackPanel Grid.Row="3" Orientation="Horizontal" Spacing="8" Margin="2,8,0,0">"""
empty_hint = """                    <StackPanel x:Name="EmptyHint" Grid.Row="2" Margin="10,26,10,0" Spacing="8"
                                VerticalAlignment="Top" Visibility="Collapsed">
                        <FontIcon Glyph="&#xE710;" FontSize="22" HorizontalAlignment="Center"
                                  Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>
                        <TextBlock Text="还没有会话" FontSize="13" FontWeight="SemiBold"
                                   HorizontalAlignment="Center"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
                        <TextBlock Text="点击右上角 + 新建连接，或用「导入密码文件」批量导入"
                                   FontSize="11.5" TextWrapping="Wrap" TextAlignment="Center"
                                   Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>
                    </StackPanel>

"""
if anchor in text and "EmptyHint" not in text:
    text = text.replace(anchor, empty_hint + anchor)
    print("空状态提示: 已插入")
else:
    print("!! 空状态提示: 未插入 (anchor 或已存在)")

if text != orig:
    open(XAML, "w", encoding="utf-8").write(text)
    print(f"MainWindow.xaml: {len(orig)} -> {len(text)} 字符")
else:
    print("MainWindow.xaml: 无改动")
