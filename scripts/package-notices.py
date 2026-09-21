#!/usr/bin/env python3
"""Generate redistributable notices from the exact restored graph and pinned license sources."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
LICENSE_FILES = {
    "DocumentFormat.OpenXml": ("3.5.1", "MIT", "OpenXML.txt"),
    "DocumentFormat.OpenXml.Framework": ("3.5.1", "MIT", "OpenXML.txt"),
    "Markdig": ("0.45.0", "BSD-2-Clause", "Markdig.txt"),
    "PdfPig": ("0.1.16", "Apache-2.0", "PdfPig.txt"),
    "System.IO.Packaging": ("10.0.2", "MIT", "System.IO.Packaging.txt"),
    "YamlDotNet": ("16.3.0", "MIT", "YamlDotNet.txt"),
}


def lock_dependencies(assets_path):
    """The committed lock file beside the project, flattened to one entry per package name."""
    lock = json.loads((assets_path.parent.parent / "packages.lock.json").read_text(encoding="utf-8-sig"))
    return {name: entry for framework in lock["dependencies"].values() for name, entry in framework.items()}


def metadata(path):
    tree = ET.parse(path)
    return {element.tag.rsplit("}", 1)[-1]: element for element in tree.iter()}


def generate(assets_path, output):
    assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
    folders = [Path(p) for p in assets["packageFolders"]]
    def package_path(relative):
        for folder in folders:
            if (folder / relative).is_dir():
                return folder / relative
        raise ValueError("Resolved package missing from cache: " + relative)
    destination = output / "licenses"
    destination.mkdir(parents=True, exist_ok=True)
    sources = json.loads((ROOT / "packaging/licenses/sources.json").read_text())
    for source in sources:
        source_file = ROOT / "packaging/licenses" / source["file"]
        if hashlib.sha256(source_file.read_bytes()).hexdigest() != source["sha256"]:
            raise ValueError("Pinned license changed: " + source["file"])
        shutil.copyfile(source_file, destination / source["file"])
    shutil.copyfile(ROOT / "packaging/licenses/sources.json", destination / "sources.json")
    entries, tooling = [], []
    found = set()
    for identity, library in assets["libraries"].items():
        if library["type"] != "package":
            continue
        name, version = identity.split("/", 1)
        folder = package_path(library["path"])
        meta = metadata(next(folder.glob("*.nuspec")))
        license_name = meta["license"].text
        if name == "Microsoft.NET.ILLink.Tasks":
            tooling.append(f"{identity}: {license_name}; build tooling, not redistributed as runtime.")
            continue
        if name not in LICENSE_FILES:
            raise ValueError("Missing reviewed license mapping: " + identity)
        expected_version, expected_license, license_file = LICENSE_FILES[name]
        if version != expected_version or license_name != expected_license:
            raise ValueError("License/version changed; review required: " + identity)
        found.add(name)
        entries.append(f"{identity} — {license_name}; licenses/{license_file}")
    print("Resolved packages: " + (", ".join(sorted(assets["libraries"])) or "none"))
    # A package the target framework already provides is pruned from the restore graph and is not
    # redistributed, so it gets no package entry. A direct reference must never disappear this way.
    direct = {name for name, entry in lock_dependencies(assets_path).items() if entry.get("type") == "Direct"}
    missing_direct = sorted((direct & set(LICENSE_FILES)) - found)
    if missing_direct:
        raise ValueError("Directly referenced packages are missing from the resolved graph: " + ", ".join(missing_direct))
    for name in sorted(set(LICENSE_FILES) - found):
        expected_version, expected_license, license_file = LICENSE_FILES[name]
        print(f"Framework-provided on this target, not redistributed: {name}/{expected_version}")
        entries.append(f"{name}/{expected_version} — {expected_license}; provided by the target framework on this build and "
                       f"not shipped as a separate package. Reviewed license retained for reference: licenses/{license_file}")
    # Runtime packs are downloadDependencies, not NuGet library references.
    downloads = {x["name"]: x["version"].strip("[]").split(",")[0].strip()
                 for framework in assets["project"]["frameworks"].values()
                 for x in framework.get("downloadDependencies", [])}
    for name, license_file in [("Microsoft.NETCore.App.Runtime.win-x64", "LICENSE.TXT"),
                               ("Microsoft.WindowsDesktop.App.Runtime.win-x64", "LICENSE")]:
        version = downloads[name]
        folder = package_path(name.lower() + "/" + version)
        meta = metadata(next(folder.glob("*.nuspec")))
        target = name + "-LICENSE.txt"
        shutil.copyfile(folder / license_file, destination / target)
        entries.append(f"{name}/{version} — {meta['license'].text}; licenses/{target}")
        if name.startswith("Microsoft.NETCore"):
            target = name + "-THIRD-PARTY-NOTICES.txt"
            shutil.copyfile(folder / "THIRD-PARTY-NOTICES.TXT", destination / target)
            entries.append("Bundled .NET native/runtime notices: licenses/" + target)
        else:
            if meta["repository"].get("commit") != "95017c711e6afc1085133d440e42b4bd78155701":
                raise ValueError("WindowsDesktop source changed; refresh WPF/WinForms notices.")
            entries.append("Windows desktop native/component notices: licenses/WPF-NOTICES.txt and licenses/WinForms-NOTICES.txt")
    text = "Grimoire third-party notices\n\nResolved application and self-contained runtime components:\n\n"
    text += "\n".join(entries) + "\n\nLicense source URLs and SHA256 values: licenses/sources.json\n"
    text += "\nBuild tools (not bundled application dependencies):\n" + "\n".join(tooling)
    text += "\nInno Setup builds the installer; its compiler is not bundled. The generated installer contains the Inno Setup engine.\n"
    text += "Inno Setup license and distribution terms: https://jrsoftware.org/files/is/license.txt\n"
    (output / "THIRD-PARTY-NOTICES.txt").write_text(text, encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, default=ROOT / "app/Grimoire/obj/project.assets.json")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(args.assets, args.output)
    print("PASS: exact restored package/runtime licenses and source-pinned notices generated.")
