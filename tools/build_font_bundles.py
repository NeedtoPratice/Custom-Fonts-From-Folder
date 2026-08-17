#!/usr/bin/env python3
"""Build Unity Font AssetBundles for the Custom Fonts From Folder mod.

Scans <mod>/fonts for .ttf / .otf / .ttc files and creates a companion
"<font file>.fontbundle" for each of them. The RimWorld mod prefers these
bundles because the Flatpak/Steam Linux environment cannot see the host's
installed fonts.

Requires the Python package UnityPy. If it is missing, the script will try
to install it with pip automatically (disable with --no-install).
"""

import argparse
import hashlib
import re
import subprocess
import sys
from pathlib import Path

MOD_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_FONTS_DIR = MOD_ROOT / "fonts"
DEFAULT_TEMPLATE = Path(__file__).resolve().parent / "fontbundle_template"
SUPPORTED_EXTENSIONS = {".ttf", ".otf", ".ttc"}


def ensure_unitypy(auto_install=True):
    try:
        import UnityPy  # noqa: F401
        return
    except ImportError:
        pass

    if not auto_install:
        sys.exit("UnityPy is not installed. Run: python3 -m pip install UnityPy")

    print("UnityPy is not installed. Trying to install it with pip ...")
    command = [sys.executable, "-m", "pip", "install", "--user", "UnityPy"]
    try:
        subprocess.check_call(command)
    except subprocess.CalledProcessError:
        sys.exit(
            "Automatic installation failed. Please run manually:\n"
            "  python3 -m pip install --user UnityPy"
        )

    # Clear cached failed imports before retrying.
    for name in list(sys.modules):
        if name == "UnityPy" or name.startswith("UnityPy."):
            del sys.modules[name]

    try:
        import UnityPy  # noqa: F401
    except ImportError:
        sys.exit("UnityPy was installed but still could not be imported.")


def slugify(name):
    value = re.sub(r"[^A-Za-z0-9_.-]+", "-", name).strip("-")
    if not value:
        value = "font"
    return value


def font_names_with_fonttools(path):
    try:
        from fontTools.ttLib import TTFont
    except ImportError:
        return None

    try:
        font = TTFont(str(path), fontNumber=0, lazy=True)
        name_table = font["name"]
        family = name_table.getDebugName(16) or name_table.getDebugName(1)
        style = name_table.getDebugName(17) or name_table.getDebugName(2)
        if family:
            return family, (style or "Regular")
    except Exception:
        return None
    finally:
        try:
            font.close()
        except Exception:
            pass

    return None


def font_names_with_fc_scan(path):
    try:
        completed = subprocess.run(
            ["fc-scan", "--format", "%{family}\\n%{style}\\n", str(path)],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=30,
            check=False,
        )
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return None

    if completed.returncode != 0:
        return None

    lines = [line.strip() for line in completed.stdout.decode("utf-8", "replace").splitlines()]
    family = next((line for line in lines if line), None)
    style = None
    if len(lines) > 1 and lines[1]:
        style = lines[1]

    if family:
        return family, (style or "Regular")
    return None


def get_font_names(path):
    result = font_names_with_fonttools(path)
    if result:
        return result
    result = font_names_with_fc_scan(path)
    if result:
        return result
    return path.stem, "Regular"


def font_file_hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_hash_file(path):
    if not path.exists():
        return None
    try:
        text = path.read_text(encoding="utf-8").strip()
        return text.split()[0] if text else None
    except OSError:
        return None


def write_hash_file(path, digest):
    path.write_text(digest + "\n", encoding="utf-8")


def bundle_is_current(font_path, bundle_path, digest):
    hash_path = Path(str(bundle_path) + ".sha256")
    if not bundle_path.exists() or bundle_path.stat().st_size == 0:
        return False
    return read_hash_file(hash_path) == digest


def build_bundle(font_path, bundle_path, family, style, template_path):
    import UnityPy

    data = font_path.read_bytes()
    stem = font_path.stem
    bundle_name = slugify(stem)
    internal_name = f"CAB-{bundle_name}"

    env = UnityPy.load(str(template_path))
    bundle = next(iter(env.files.values()))

    font_obj = None
    asset_bundle_obj = None
    for obj in env.objects:
        if obj.type.name == "Font" and font_obj is None:
            font_obj = obj
        elif obj.type.name == "AssetBundle" and asset_bundle_obj is None:
            asset_bundle_obj = obj

    if font_obj is None or asset_bundle_obj is None:
        raise RuntimeError("Template bundle does not contain Font and AssetBundle objects.")

    font = font_obj.read()
    font.m_FontData = list(data)
    font.m_FontNames = [family]
    font.m_Name = f"{slugify(family)}-{slugify(style)}-{bundle_name}"
    font.m_FontSize = 16.0
    font.save()

    asset_bundle = asset_bundle_obj.read()
    asset_bundle.m_Name = bundle_name
    asset_bundle.m_AssetBundleName = bundle_name
    new_container_key = f"assets/fonts/{bundle_name}.ttf"
    asset_bundle.m_Container = [
        (new_container_key, info) for _, info in asset_bundle.m_Container
    ]
    asset_bundle.save()

    # Unity refuses to load two AssetBundles whose inner serialized file names
    # match. Give each bundle a unique inner file name as well.
    serialized_file = next(iter(bundle.files.values()))
    serialized_file.name = internal_name
    bundle.files = {internal_name: serialized_file}

    bundle_path.write_bytes(bundle.save(packer="lz4"))
    hash_path = Path(str(bundle_path) + ".sha256")
    write_hash_file(hash_path, hashlib.sha256(data).hexdigest())


def clean_orphaned_bundles(fonts_dir):
    removed = []
    for bundle_path in sorted(fonts_dir.rglob("*.fontbundle")):
        font_path = Path(str(bundle_path)[: -len(".fontbundle")])
        if not font_path.exists():
            bundle_path.unlink()
            removed.append(bundle_path)

            hash_path = Path(str(bundle_path) + ".sha256")
            if hash_path.exists():
                hash_path.unlink()
    return removed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fonts-dir", type=Path, default=DEFAULT_FONTS_DIR,
                        help="Fonts directory to scan (default: <mod>/fonts)")
    parser.add_argument("--template", type=Path, default=DEFAULT_TEMPLATE,
                        help="FontBundle template to use")
    parser.add_argument("--force", action="store_true",
                        help="Rebuild all bundles even if they are current")
    parser.add_argument("--clean", action="store_true",
                        help="Remove bundles whose source font file is missing")
    parser.add_argument("--no-install", action="store_true",
                        help="Do not attempt to install UnityPy automatically")
    args = parser.parse_args()

    ensure_unitypy(auto_install=not args.no_install)

    fonts_dir = args.fonts_dir
    if not fonts_dir.is_dir():
        sys.exit(f"Fonts directory does not exist: {fonts_dir}")

    template_path = args.template
    if not template_path.is_file():
        sys.exit(f"Template bundle does not exist: {template_path}")

    font_files = sorted(
        path for path in fonts_dir.rglob("*")
        if path.is_file() and path.suffix.lower() in SUPPORTED_EXTENSIONS
    )

    if not font_files:
        print(f"No .ttf/.otf/.ttc files found in {fonts_dir}")
    else:
        print(f"Found {len(font_files)} font file(s) in {fonts_dir}")

    built = []
    skipped = []
    failed = []

    for font_path in font_files:
        bundle_path = Path(str(font_path) + ".fontbundle")
        digest = font_file_hash(font_path)

        if not args.force and bundle_is_current(font_path, bundle_path, digest):
            skipped.append(font_path)
            print(f"SKIP   {font_path.name} (bundle already current)")
            continue

        family, style = get_font_names(font_path)
        print(f"BUILD  {font_path.name}  family={family!r} style={style!r}")

        try:
            build_bundle(font_path, bundle_path, family, style, template_path)
            built.append(font_path)
            print(f"       -> {bundle_path.name} ({bundle_path.stat().st_size} bytes)")
        except Exception as exc:
            failed.append(font_path)
            print(f"ERROR  {font_path.name}: {exc}")

    if args.clean:
        removed = clean_orphaned_bundles(fonts_dir)
        for bundle_path in removed:
            print(f"CLEAN  removed orphan bundle {bundle_path.relative_to(fonts_dir)}")

    print()
    print(f"Built:   {len(built)}")
    print(f"Skipped: {len(skipped)}")
    print(f"Failed:  {len(failed)}")
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
