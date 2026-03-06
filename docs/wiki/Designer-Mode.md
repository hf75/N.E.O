# Designer Mode

Designer Mode lets you click on any UI element in your generated app and edit its properties visually — no prompts needed.

## Activating Designer Mode

1. Click the **Designer Mode** button in the toolbar (pencil icon)
2. N.E.O. will inject design IDs into your code and recompile
3. The preview now responds to clicks

## Using Designer Mode

1. **Click** any element in the preview (button, text, panel, etc.)
2. A **Properties Window** appears near your cursor
3. Edit properties:
   - **Text / Content**: Change displayed text
   - **Font**: Family, size, weight, style
   - **Colors**: Foreground and background (with color picker)
   - **Spacing**: Margin and padding
4. Click **Apply** to compile and see your changes instantly

Each edit creates a **history entry** — you can undo with Ctrl+Shift+Z.

## How It Works Internally

1. **ID Injection**: Roslyn analyzes your code and wraps every UI control creation with a `.RegisterDesignId("__neo_XXXX")` call
2. **Hit Testing**: When you click in the preview, the child process walks the visual tree upward to find the nearest element with a design ID
3. **Property Extraction**: The element's current properties are read via reflection and sent back over IPC
4. **Code Patching**: Your edits are applied directly to the source code AST using Roslyn — modifying object initializers or adding property assignments
5. **Recompilation**: The patched code is compiled and hot-reloaded

## Limitations

- Only properties shown in the Properties Window can be edited (Text, Font, Colors, Margin/Padding)
- Width, Height, and alignment are not yet editable via Designer Mode (use prompts instead)
- Only elements created with `new SomeControl()` syntax receive design IDs
- One element at a time (no multi-select)

## Deactivating Designer Mode

Click the **Designer Mode** button again. All `__neo_` design IDs are automatically removed from your code, and it recompiles cleanly.
