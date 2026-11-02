namespace Neo.AssemblyForge;

/// <summary>
/// How generated code reads numbers from text. A tool runs with the number format of the PC it is on, where a
/// point can be the thousands separator: a model that writes "1.08" as the start of a text box and reads it with
/// the current culture gets 108 or nothing, seen on a German PC. One text for all surfaces.
/// </summary>
public static class NumberGuidance
{
    public const string Rule =
        "Numbers in text: a tool runs with the number format of the PC it is on, where a comma can be the decimal sign and a point can stand between thousands, so \"1.08\" read with the current culture is 108 or nothing. " +
        "Read every number from a text box, or a start value that stands in one, like this: where both , and . occur, the last of them is the decimal sign and the other is removed; otherwise a , counts as a .; then parse with NumberStyles.Float and CultureInfo.InvariantCulture. " +
        "Then 1.5, 1,5, 1.234,5 and 1,234.5 are read right on every PC. Show results with the current culture, the way this PC writes numbers. ";
}
