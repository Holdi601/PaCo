"""Converts site/img/*.png to WebP (smaller pages on phones) and removes the PNGs."""
import glob, os
from PIL import Image
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
for p in glob.glob(os.path.join(ROOT, "site", "img", "*.png")):
    Image.open(p).save(p[:-4] + ".webp", "WEBP", quality=84, method=6)
    os.remove(p)
print("ok")
