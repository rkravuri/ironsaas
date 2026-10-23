/** A column's name as words: "bank_account" reads "Bank account". */
export const fieldWords = (field: string) => {
  const words = field.replaceAll("_", " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
};
