# Repository instructions

## README and documentation prose

Use the `humanizer` skill when reviewing or editing prose in `README*.md` and
`docs/**/*.md`. Apply it after the factual content is drafted, and make only
changes that are supported by the source document, current code, or test
results.

- Keep technical facts, limitations, uncertainty, and implementation status
  unchanged. Do not describe planned or unfinished work as complete.
- Prefer concrete behavior, requirements, commands, and known limitations over
  promotional or generic explanatory text.
- Preserve useful Markdown structure and all code blocks, inline code,
  commands, paths, URLs, identifiers, and data. Do not add bold text, emoji,
  lists, or horizontal rules unless they improve navigation or accuracy.
- In English, use neutral technical prose. Avoid inflated claims, stock
  transitions, repeated `not just ... but ...` contrasts, and conclusions that
  only restate the preceding section.
- In Japanese, write natural Japanese instead of copying English sentence
  structure. Avoid repeated forms such as `単なる〜ではなく`, `〜を実現する`,
  unnecessary emphasis, and literal translations when a shorter technical
  expression says the same thing.
- Use detect-only mode when another open pull request owns the target file, or
  when the task asks for findings rather than edits. Check open pull requests
  before making broad README changes.
- Do not apply this prose-editing skill to source code, configuration files,
  generated data, or quoted third-party text unless the task explicitly asks
  for it.
