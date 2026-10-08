import { Chip, Tooltip } from '@mui/material';
import type { SqlErrorCategory } from '../../types/sqlErrors';

const labels: Record<SqlErrorCategory, string> = {
  TIMEOUT: 'Timeout',
  DEADLOCK: 'Deadlock',
  CONNECTION: 'Connection',
  LOGIN_FAILURE: 'Login Failure',
  PERMISSION_DENIED: 'Permission Denied',
  CONSTRAINT_VIOLATION: 'Constraint Violation',
  CONVERSION_ERROR: 'Conversion Error',
  ARITHMETIC_ERROR: 'Arithmetic Error',
  INVALID_OBJECT: 'Invalid Object',
  INVALID_COLUMN: 'Invalid Column',
  TRANSACTION_ERROR: 'Transaction Error',
  SQL_EXECUTION_ERROR: 'SQL Execution Error',
  UNKNOWN: 'Unknown',
};

const colors: Record<SqlErrorCategory, 'error' | 'warning' | 'info' | 'default'> = {
  TIMEOUT: 'error',
  DEADLOCK: 'error',
  CONNECTION: 'error',
  LOGIN_FAILURE: 'warning',
  PERMISSION_DENIED: 'warning',
  CONSTRAINT_VIOLATION: 'warning',
  CONVERSION_ERROR: 'warning',
  ARITHMETIC_ERROR: 'warning',
  INVALID_OBJECT: 'warning',
  INVALID_COLUMN: 'warning',
  TRANSACTION_ERROR: 'warning',
  SQL_EXECUTION_ERROR: 'info',
  UNKNOWN: 'default',
};

interface Props {
  category: SqlErrorCategory;
}

export default function SqlErrorCategoryBadge({ category }: Props) {
  const label = labels[category] ?? category;

  return (
    <Tooltip title={`Normalized category: ${category}`} arrow>
      <Chip label={label} size="small" color={colors[category] ?? 'default'} variant="outlined" />
    </Tooltip>
  );
}

/** Display label for a category, for use outside a chip. */
export function sqlErrorCategoryLabel(category: SqlErrorCategory): string {
  return labels[category] ?? category;
}

/** Every category, in the order the filter dropdown offers them. */
export const SQL_ERROR_CATEGORIES = Object.keys(labels) as SqlErrorCategory[];
