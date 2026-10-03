/** New mutation inputs only: UTF-16 code units before trimming, matching .NET String.Length. */
export const ACCOUNT_TEXT_LIMITS = Object.freeze({ alias: 64, name: 256, notes: 2048 });

export function validateAccountText(input: { name?: string; alias?: string; notes?: string }): void {
  for (const field of ['name', 'alias', 'notes'] as const) {
    const value = input[field];
    if (typeof value === 'string' && value.length > ACCOUNT_TEXT_LIMITS[field]) {
      throw new Error(`Account ${field} must not exceed ${ACCOUNT_TEXT_LIMITS[field]} UTF-16 code units.`);
    }
  }
}
