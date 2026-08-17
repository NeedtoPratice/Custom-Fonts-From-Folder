Put font files in this folder.

Supported source file extensions:
- .ttf
- .otf
- .ttc

Subfolders are also scanned.

How to add a new font:
1. Copy the font file into this folder, for example:
   Custom Fonts From Folder/fonts/MyFont.ttf

2. Run the automatic bundle builder from the mod root:
   python3 tools/build_font_bundles.py

   The script needs the Python package UnityPy:
   python3 -m pip install --user UnityPy

   It will create:
   Custom Fonts From Folder/fonts/MyFont.ttf.fontbundle

3. Restart RimWorld and select the font in:
   Options > Mod Settings > Custom Fonts From Folder

Currently included:
- sarasa-fixed-sc-regular-nerd-font.ttf
- sarasa-fixed-sc-bold-nerd-font.ttf
- NotoSans-Regular.ttf
- NotoSansCJK-VF.ttc
