'use strict';

// UInt16 で表現できる最大の学籍番号。実運用の最大値は 26300 だが上限として保持する。
const MAX_STUDENT_NUMBER = 65535;

/**
 * 入力コードから学籍番号(後方5桁)を抽出する。
 * バーコード(10桁)でも手入力(5桁)でも、数字以外を除去して後方5桁を採用する。
 * 5桁未満・数値化できない場合は null を返す。
 */
function normalizeCode(input) {
  if (input === null || input === undefined) return null;
  const digits = String(input).replace(/\D/g, '');
  if (digits.length < 5) return null;
  const last5 = digits.slice(-5);
  const value = Number.parseInt(last5, 10);
  if (!Number.isFinite(value)) return null;
  return value;
}

module.exports = { normalizeCode, MAX_STUDENT_NUMBER };
