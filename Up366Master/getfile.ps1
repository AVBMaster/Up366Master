# 获取当前目录下的所有文件（不包含子目录）
Get-ChildItem -File | ForEach-Object {
    # 读取文件内容（使用 UTF-8 编码避免乱码）
    $fileContent = Get-Content -Path $_.FullName -Raw -Encoding UTF8
    
    # 输出格式化结果
    "$($_.Name): $fileContent"
}