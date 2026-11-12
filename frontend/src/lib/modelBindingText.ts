/**
 * ASP.NET answers a request body it could not bind (a missing or mistyped property) with the binder's own sentences:
 * "The req field is required." and "The JSON value could not be converted to …Request. Path: $.effectiveFrom | LineNumber…".
 * They name a parameter and a CLR type, not something a person can act on, so a screen shows its own sentence instead.
 * A domain refusal ("The reason field is required." from a DataAnnotation on a named property starts upper-case) is not matched.
 */
export function isModelBindingText(text: string): boolean {
  return /could not be converted to|\bJSON value\b|\bPath: \$|\bLineNumber\b|\bThe [a-z][A-Za-z]* field is required/.test(text);
}
