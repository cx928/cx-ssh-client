#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""最后一轮美化:
   1) 清理被改写的 Button 上多余的 Content 属性 (与子元素内容重复)
   2) 会话卡片左侧增加协议色条
   3) 欢迎页补充「账号同步」入口
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = r"E:\Documents\deepseek-harness\default-workspace\程星SSH客户端"
files = [os.path.join(BASE, "MainWindow.xaml")] + [
    os.path.join(BASE, "Views", f) for f in
    ("RdpView.xaml", "SettingsView.xaml", "FileTransferView.xaml", "TerminalView.xaml", "VncView.xaml")
]

# ---------- 1. 清理多余 Content 属性 ----------
pattern = re.compile(
    r'(<Button\b[^>]*?)\s+Content="[^"]*"([^>]*>)\s*\n\s*(<StackPanel Orientation="Horizontal" Spacing="8">)',
    re.S)

total_cleaned = 0
for path in files:
    if not os.path.exists(path):
        continue
    text = open(path, encoding="utf-8").read()
    new, n = pattern.subn(lambda m: f'{m.group(1)}{m.group(2)}\n{m.group(3)}', text)
    if n:
        open(path, "w", encoding="utf-8").write(new)
        total_cleaned += n
        print(f"{os.path.basename(path)}: 清理 {n} 处冗余 Content")
print(f"共清理 {total_cleaned} 处")

# ---------- 2. 会话卡片协议色条 ----------
mw = os.path.join(BASE, "MainWindow.xaml")
text = open(mw, encoding="utf-8").read()
old_icon = """                                    <Border Grid.Column="0" Width="36" Height="36" CornerRadius="9"
                                            VerticalAlignment="Center"
                                            Background="{x:Bind ProtocolSoftBrush}">
                                        <FontIcon Glyph="{x:Bind ProtocolGlyph}"
                                                  FontSize="15"
                                                  Foreground="{x:Bind ProtocolBrush}"/>
                                    </Border>"""
new_icon = """                                    <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="10"
                                                VerticalAlignment="Center">
                                        <Border Width="3" Height="34" CornerRadius="2"
                                                Background="{x:Bind ProtocolBrush}"/>
                                        <Border Width="36" Height="36" CornerRadius="9"
                                                Background="{x:Bind ProtocolSoftBrush}">
                                            <FontIcon Glyph="{x:Bind ProtocolGlyph}"
                                                      FontSize="15"
                                                      Foreground="{x:Bind ProtocolBrush}"/>
                                        </Border>
                                    </StackPanel>"""
if old_icon in text:
    text = text.replace(old_icon, new_icon)
    print("会话卡片: 已加入协议色条")
else:
    print("!! 会话卡片: 图标块未匹配")

# ---------- 3. 欢迎页补充账号同步入口 ----------
old_btns = """                            <Button Click="Import_Click">
                            <StackPanel Orientation="Horizontal" Spacing="8"><FontIcon Glyph="&#xE898;" FontSize="14"/><TextBlock Text="导入密码文件"/></StackPanel>
                            </Button>
                        </StackPanel>"""
new_btns = """                            <Button Click="Import_Click">
                            <StackPanel Orientation="Horizontal" Spacing="8"><FontIcon Glyph="&#xE898;" FontSize="14"/><TextBlock Text="导入密码文件"/></StackPanel>
                            </Button>
                            <Button Click="SyncNow_Click">
                            <StackPanel Orientation="Horizontal" Spacing="8"><FontIcon Glyph="&#xE895;" FontSize="14"/><TextBlock Text="账号同步"/></StackPanel>
                            </Button>
                        </StackPanel>"""
if old_btns in text:
    text = text.replace(old_btns, new_btns)
    print("欢迎页: 已加入账号同步入口")
else:
    print("!! 欢迎页: 按钮组未匹配")

open(mw, "w", encoding="utf-8").write(text)
