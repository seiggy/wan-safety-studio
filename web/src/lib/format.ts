export const plural = (count: number, word: string): string => `${count} ${word}${count === 1 ? '' : 's'}`;
