import { useMemo, type RefObject } from 'react';
import { buildTimeIncrementOptions } from '../utils/timeEntry';

interface TimeIncrementSelectProps {
  value: string;
  onChange: (value: string) => void;
  className?: string;
  inputRef?: RefObject<HTMLSelectElement | null>;
  placeholder?: string;
}

export function TimeIncrementSelect({
  value,
  onChange,
  className,
  inputRef,
  placeholder = 'Select time',
}: TimeIncrementSelectProps) {
  const options = useMemo(() => buildTimeIncrementOptions(), []);

  return (
    <select
      ref={inputRef}
      className={className}
      value={value}
      onChange={(e) => onChange(e.target.value)}
    >
      <option value="">{placeholder}</option>
      {options.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}
