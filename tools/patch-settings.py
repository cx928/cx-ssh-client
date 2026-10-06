#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""设置页显示数据目录(密码库位置)"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = r"E:\Documents\deepseek-harness\default-workspace\cx-ssh-client\Views"
XAML = os.path.join(BASE, "SettingsView.xaml")
CS = os.path.join(BASE, "SettingsView.xaml.cs")

# ---------- XAML ----------
xaml = open(XAML, encoding="utf-8").read()
anchor = """                        <StackPanel Orientation="Horizontal" Spacing="10">
                            <Button Content="打开数据目录" Click="OpenDataDir_Click">"""
block = """                        <Border Background="{ThemeResource SubtleFillColorSecondaryBrush}"
                                CornerRadius="8" Padding="10,8">
                            <StackPanel Spacing="2">
                                <TextBlock Text="数据目录（密码库 / 设置 / 日志的存放位置）" FontSize="11"
                                           Foreground="{ThemeResource TextFillColorTertiaryBrush}"/>
                                <TextBlock x:Name="DataDirText" FontSize="11.5" TextWrapping="Wrap"
                                           IsTextSelectionEnabled="True"/>
                            </StackPanel>
                        </Border>

"""
if "DataDirText" in xaml:
    print("XAML: 已存在 DataDirText, 跳过")
elif anchor in xaml:
    xaml = xaml.replace(anchor, block + anchor)
    open(XAML, "w", encoding="utf-8").write(xaml)
    print("XAML: 已插入数据目录显示")
else:
    print("!! XAML: 未找到锚点")

# ---------- 代码 ----------
cs = open(CS, encoding="utf-8").read()
if "DataDirText" in cs:
    print("CS: 已存在赋值, 跳过")
else:
    target = """        var when = cfg.LastSyncAt == 0"""
    insert = """        try
        {
            if (DataDirText is not null)
                DataDirText.Text = VaultService.DataDirectory
                    + (VaultService.IsWritable(VaultService.DataDirectory) ? "" : "  ⚠️ 该目录不可写");
        }
        catch { }

"""
    if target in cs:
        cs = cs.replace(target, insert + target)
        open(CS, "w", encoding="utf-8").write(cs)
        print("CS: 已插入数据目录赋值")
    else:
        print("!! CS: 未找到锚点")
