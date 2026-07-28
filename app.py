import os

os.environ["PHOTOLOG_WEB"] = "1"

from main import demo  # noqa: E402  (env must be set before main builds the UI)

if __name__ == "__main__":
    demo.launch()
