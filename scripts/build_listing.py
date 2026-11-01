"""Builds the VPM listing and its web page from the manifests attached to the GitHub releases.

Every release keeps a <name>-<version>.json asset, so old versions stay installable from VCC.

Usage: python scripts/build_listing.py --repo sdosker/dosker-tools --out site
"""
import argparse
import html
import json
import os
import sys
import urllib.parse
import urllib.request
from pathlib import Path

LISTING_NAME = "Dosker Tools"
LISTING_ID = "com.dosker.tools"
AUTHOR = "Dosker"
DISPLAY_PREFIX = "Dosker Tools - "
PAGE_TEMPLATE = Path(__file__).with_name("listing_page.html")
# Installs every tool, so its dependencies aren't support packages
META_PACKAGE = "com.dosker.all"


def fetch(url, token):
    headers = {"Accept": "application/vnd.github+json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers)) as response:
        return json.load(response)


def read_manifests(repo, token):
    manifests = []
    page = 1
    while True:
        releases = fetch(f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}", token)
        if not releases:
            return manifests
        for release in releases:
            if release["draft"] or release["prerelease"]:
                continue
            for asset in release["assets"]:
                if asset["name"].endswith(".json"):
                    manifests.append(fetch(asset["browser_download_url"], token))
        page += 1


def version_key(version):
    return [int(part) for part in version.split("-")[0].split(".")]


def build_listing(manifests, listing_url):
    packages = {}
    for manifest in manifests:
        versions = packages.setdefault(manifest["name"], {"versions": {}})["versions"]
        versions[manifest["version"]] = manifest
    return {"name": LISTING_NAME, "id": LISTING_ID, "url": listing_url, "author": AUTHOR, "packages": packages}


def package_tag(manifest, required):
    if manifest["name"] == META_PACKAGE:
        return "Tudo"
    if manifest["name"] in required:
        return "Dependência"
    return "Avatars" if "com.vrchat.avatars" in manifest.get("vpmDependencies", {}) else "Avatars e Worlds"


def license_label(manifest):
    license_id = manifest.get("license", "")
    return "Não comercial" if license_id.startswith("SEE LICENSE") else license_id


def render_package(name, package, required, repo):
    versions = sorted(package["versions"], key=version_key, reverse=True)
    manifest = package["versions"][versions[0]]
    display = manifest["displayName"].removeprefix(DISPLAY_PREFIX)
    description = manifest.get("description", "")
    dependencies = ", ".join(f"{dep} {range_}" for dep, range_ in manifest.get("vpmDependencies", {}).items())
    older = ", ".join(
        f'<a href="{html.escape(package["versions"][version]["url"])}">{html.escape(version)}</a>'
        for version in versions[1:]
    )
    older_row = f"<dt>Versões anteriores</dt><dd>{older}</dd>" if older else ""
    docs = f"https://github.com/{repo}/tree/main/Packages/{name}#readme"
    changelog = f"https://github.com/{repo}/blob/main/Packages/{name}/CHANGELOG.md"
    return f"""      <article class="pkg" data-search="{html.escape(f"{display} {name} {description}".lower())}">
        <div class="pkg-head">
          <h3>{html.escape(display)}</h3>
          <span class="version mono">{html.escape(versions[0])}</span>
          <span class="tag">{html.escape(package_tag(manifest, required))}</span>
        </div>
        <code class="pkg-id">{html.escape(name)}</code>
        <p>{html.escape(description)}</p>
        <div class="pkg-actions">
          <a class="btn" href="{html.escape(manifest["url"])}">Baixar .zip</a>
          <a href="{html.escape(docs)}">Documentação</a>
          <a href="{html.escape(changelog)}">Mudanças</a>
        </div>
        <details>
          <summary>Detalhes</summary>
          <dl>
            <dt>Dependências</dt><dd class="mono">{html.escape(dependencies) or "Nenhuma"}</dd>
            <dt>Licença</dt><dd>{html.escape(license_label(manifest))}</dd>
            {older_row}
          </dl>
        </details>
      </article>"""


def build_page(listing, repo):
    latest = {
        name: package["versions"][max(package["versions"], key=version_key)]
        for name, package in listing["packages"].items()
    }
    required = {
        dep for name, manifest in latest.items() if name != META_PACKAGE
        for dep in manifest.get("vpmDependencies", {})
    }
    names = sorted(latest, key=lambda name: (name in required, latest[name]["displayName"]))
    cards = "\n".join(render_package(name, listing["packages"][name], required, repo) for name in names)

    add_url = "vcc://vpm/addRepo?url=" + urllib.parse.quote(listing["url"], safe="")
    page = PAGE_TEMPLATE.read_text(encoding="utf-8")
    for token, value in [
        ("__LISTING_NAME__", LISTING_NAME),
        ("__LISTING_URL__", listing["url"]),
        ("__ADD_URL__", add_url),
        ("__AUTHOR__", AUTHOR),
        ("__REPO__", repo),
    ]:
        page = page.replace(token, html.escape(value))
    return page.replace("__PACKAGES__", cards)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    owner, name = args.repo.split("/")
    listing_url = f"https://{owner}.github.io/{name}/index.json"
    manifests = read_manifests(args.repo, os.environ.get("GITHUB_TOKEN"))
    if not manifests:
        print("ERROR: No released packages found", file=sys.stderr)
        sys.exit(1)

    listing = build_listing(manifests, listing_url)
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "index.json").write_text(json.dumps(listing, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (args.out / "index.html").write_text(build_page(listing, args.repo), encoding="utf-8")
    print(f"{len(listing['packages'])} packages, {len(manifests)} versions")


if __name__ == "__main__":
    main()
