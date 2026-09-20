# Delta for Customer Registry

## ADDED Requirements

### Requirement: Payment-Instrument Reference Is a Distinct Field from PaymentTerms

`Customer` MAY carry a payment-instrument reference field, separately
named from and independent of `PaymentTerms`. `PaymentTerms` MUST remain
unchanged, free-text, and byte-identical after this capability is added.
The instrument-reference field MUST store a tokenized/PCI-scoped
reference only; it MUST NOT store card data (no PAN, no CVV, no raw card
number) in any form.

#### Scenario: PaymentTerms is unaffected by adding an instrument reference

- GIVEN a `Customer` row has an existing `PaymentTerms` value
- WHEN a payment-instrument reference is added to that customer
- THEN `PaymentTerms` is read back byte-identical to its prior value

#### Scenario: Instrument reference stores no card data

- GIVEN a payment-instrument reference is recorded on a `Customer`
- WHEN that field is inspected
- THEN it contains a tokenized/PCI-scoped reference only, with no PAN,
  CVV, or raw card number present in any form
