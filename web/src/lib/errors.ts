type MutationError = {
  __typename?: string;
  message?: string;
  fields?: { field: string; messages: string[] }[];
};

export type FormErrors = { message: string | null; fields: Record<string, string> };

/** Typed mutation errors (GQ-3) → one summary message plus per-field messages keyed by lower-camel field name. */
export function formErrors(errors: readonly MutationError[] | null | undefined): FormErrors | null {
  if (!errors?.length) return null;
  const fields: Record<string, string> = {};
  const messages: string[] = [];
  for (const error of errors) {
    if (error.fields?.length) {
      for (const f of error.fields)
        fields[f.field.charAt(0).toLowerCase() + f.field.slice(1)] = f.messages.join(" ");
    } else if (error.message) {
      messages.push(error.message);
    }
  }
  return {
    message: messages.join(" ") || (Object.keys(fields).length ? "Please fix the highlighted fields." : null),
    fields,
  };
}
