# Sarus documents

This folder keeps the Sarus documents with the code they describe. `pdf/` holds the finished PDFs, and
`source/` holds what they are made from: HTML pages, the print style sheet, the logo and the fonts.

None of this is compiled into the program. The three documents that do ship with it, the User Manual,
the Release Notes and the Field Test Checklist, are copied to `Sarus/docs/` under version-free names; the
build puts them in the program's `docs` folder and the installer adds a Start-menu entry for the manual.

## Rebuilding a PDF

The PDFs are printed by Microsoft Edge from the HTML sources. From `source/pdf/`, for example:

```
msedge --headless=new --disable-gpu --no-pdf-header-footer --allow-file-access-from-files ^
       --virtual-time-budget=15000 --print-to-pdf=user-manual.pdf user-manual.html
```

The pages in `source/web/` print the same way. After a rebuild, look through every page before replacing
the copy in `pdf/` or `Sarus/docs/`, because a changed paragraph can push a table onto the next page.

## Writing style

Each document goes through two passes. The first writes the content; the second edits it, starting with
`SarusTests/style/lint.py`, which flags filler phrases, emojis, bullet-heavy layouts, bold lead-ins and
sentences of monotonous length. A document is finished when the lint reports nothing and a read-through
finds nothing either.

## Fonts

Barlow Semi Condensed, Source Sans 3 and JetBrains Mono are used under the SIL Open Font License 1.1; the
licence texts are in `source/pdf/fonts/`.
